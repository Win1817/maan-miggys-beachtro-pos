using Beachtro.Application.Common;
using Beachtro.Application.Sales;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Numbering;
using Beachtro.Infrastructure.Sync;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>
/// IB-24 required drill (sync/recovery design §Restore runbook; POS-DEC-010): the shop PC is lost after
/// selling and uploading past its last backup. A brand-new PC restores that old backup. No number is
/// reused, nothing is uploaded twice, unknown identities and unfinished payments are held until the
/// owner resolves them, and selling resumes only after an explicit reopen.
/// </summary>
public sealed class RecoveryDrillTests : IAsyncLifetime
{
    private SalesHarness _lost = null!;
    private SalesHarness? _replacement;

    public async ValueTask InitializeAsync() => _lost = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _replacement?.Dispose();
        _lost.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Key(SaleReceipt r) => $"sale/{r.OrderId:D}/schema1";

    [Fact]
    public async Task An_old_backup_restored_on_a_new_pc_reuses_no_number_and_uploads_nothing_twice()
    {
        var lost = _lost;
        Ok(await lost.Sync.SetSimulatedDestinationAsync(lost.Owner, true));

        // Before the backup: two uploaded sales, one sale not yet uploaded, one checkout left prepared.
        var s1 = await lost.SellCashAsync((lost.Tea, 1));
        var s2 = await lost.SellCashAsync((lost.Cookie, 1));
        await lost.Sync.RunCycleAsync();
        var s3 = await lost.SellCashAsync((lost.Tea, 1));
        var preparedId = Guid.NewGuid();
        Ok(await lost.Checkout.PrepareAsync(lost.Owner, preparedId, SalesHarness.Cart((lost.Service, 1))));
        var backup = Ok(await lost.Backups.CreateBackupAsync(lost.Owner));

        // After the backup, on the lost PC only: s3 is uploaded with a lost acknowledgement, the prepared
        // checkout is completed (money taken), and one more sale is uploaded. Then the PC is gone.
        lost.Clock.Advance(TimeSpan.FromMinutes(30));
        lost.Remote.FailAfterCreate = new RemoteStoreException(RemoteErrorKind.Transient, "connection reset", null);
        await lost.Sync.RunCycleAsync();
        lost.Clock.Advance(TimeSpan.FromMinutes(10));
        await lost.Sync.RunCycleAsync();
        var s4 = Ok(await lost.Checkout.CompleteAsync(lost.Owner, preparedId, new CashPayment(20_00)));
        var s5 = await lost.SellCashAsync((lost.Cookie, 1));
        lost.Clock.Advance(TimeSpan.FromMinutes(10));
        await lost.Sync.RunCycleAsync();
        Assert.All(new[] { s1, s2, s3, s4, s5 }, s => Assert.Equal(1, lost.Remote.CountFor(Key(s))));
        var remoteFilesBefore = lost.Remote.Files.Count;
        Assert.EndsWith("-000005", s5.TransactionNumber, StringComparison.Ordinal);

        // A new PC, two hours later (past midnight in Manila, so a new business date), restores the backup with the owner's sign-in from the backup.
        var pc = _replacement = SalesHarness.CreateBlankDevice(lost.Remote, lost.Clock.UtcNowMilliseconds + 2 * 3_600_000);
        Assert.Equal(ResultStatus.Unauthenticated,
            (await pc.Backups.RestoreOnNewDeviceAsync(backup.PackagePath, OwnerUsername, "wrong password entirely", "PC stolen")).Status);
        var restored = Ok(await pc.Backups.RestoreOnNewDeviceAsync(backup.PackagePath, OwnerUsername, OwnerPassword, "Shop PC stolen; restoring last backup"));
        Assert.Equal(1, restored.UnresolvedPreparedRequests);
        // The backup's series and every business date since (the lost PC may have sold on them) are on hold.
        Assert.Equal([NumberSeriesService.SeriesKeyFor("2025-11-16"), NumberSeriesService.SeriesKeyFor("2025-11-17")],
            restored.BlockedSeries.Select(b => b.SeriesKey));

        // A second restore is refused: the PC is no longer brand new.
        Assert.Equal(ResultStatus.Forbidden, (await pc.Backups.RestoreOnNewDeviceAsync(backup.PackagePath, OwnerUsername, OwnerPassword, "again")).Status);

        await pc.SignInOwnerAsync();
        var status = Ok(await pc.Recovery.GetStatusAsync(pc.Owner));
        Assert.True(status.InRecovery);
        Assert.Equal(lost.Security.Database.Scalar<string>("SELECT InstallationId FROM InstallationState"),
            pc.Security.Database.Scalar<string>("SELECT InstallationId FROM InstallationState"));
        Assert.Equal(2, status.ReopenBlockers.Count);

        // Selling is blocked; nothing is uploaded while identities are unknown.
        Assert.Equal(ResultStatus.RecoveryRequired, (await pc.Checkout.PrepareAsync(pc.Owner, Guid.NewGuid(), SalesHarness.Cart((lost.Tea, 1)))).Status);
        await pc.Sync.RunCycleAsync();
        Assert.Equal(remoteFilesBefore, pc.Remote.Files.Count);
        // s3 was queued before the backup and uploaded by the lost PC after it: held, never re-created.
        // (The backup's own record is written after its snapshot, so it is not in the restored data.)
        Assert.Equal(1L, pc.Count("SyncQueue", $"State = 'RecoveryHold' AND LastErrorCode = '{SyncService.Codes.IdentityJournalStale}'"));

        // The owner closes the unfinished payment: it was completed on the lost PC (receipt -000004).
        Assert.Equal(ResultStatus.Conflict, (await pc.Backups.ReopenAsync(pc.Owner, "too early")).Status);
        var prepared = Assert.Single(status.PreparedRequests);
        Assert.Equal(20_00L, prepared.TotalMinor);
        Ok(await pc.Recovery.ClosePreparedRequestAsync(pc.Owner, prepared.RequestId, $"Completed on the lost PC as {s4.TransactionNumber} (printed receipt)"));

        // The last printed receipt proves the high-water mark of the lost day; nothing was sold after midnight.
        var today = NumberSeriesService.SeriesKeyFor("2025-11-16");
        Assert.Equal(ResultStatus.Validation, (await pc.Numbers.ResolveHoldAsync(pc.Owner, today, 2, "guess")).Status);   // below what the backup knew
        foreach (var hold in Ok(await pc.Recovery.GetStatusAsync(pc.Owner)).BlockedSeries)
        {
            var proven = hold.SeriesKey == today ? 5 : 0;
            Ok(await pc.Numbers.ResolveHoldAsync(pc.Owner, hold.SeriesKey, proven, $"Last printed receipt {s5.TransactionNumber}"));
        }

        // Held upload: the destination already has s3 (lost acknowledgement), so it is adopted, not re-created.
        var release = Ok(await pc.Sync.ReleaseHeldUploadsAsync(pc.Owner, "Checked the destination folder"));
        Assert.Equal(new Application.Sync.HoldRelease(1, 0, 0), release);
        await pc.Sync.RunCycleAsync();
        Assert.Equal(remoteFilesBefore, pc.Remote.Files.Count);
        Assert.Equal(1, pc.Remote.CountFor(Key(s3)));
        Assert.Equal(0L, pc.Count("SyncQueue", "State <> 'Succeeded'"));

        // Reopen; the lost day's series stays above every number the lost PC used, and today starts fresh.
        Ok(await pc.Backups.ReopenAsync(pc.Owner, "Series proven from printed receipts; lost period recorded"));
        Assert.Equal(5L, pc.Security.Database.Scalar<long>($"SELECT LastReservedSequence FROM NumberSeries WHERE SeriesKey = '{today}'"));
        var next = await pc.SellCashAsync((lost.Tea, 1));
        Assert.Equal("MB-20251117-000001", next.TransactionNumber);
        await pc.Sync.RunCycleAsync();
        Assert.Equal(1, pc.Remote.CountFor(Key(next)));
        Assert.Equal(remoteFilesBefore + 1, pc.Remote.Files.Count);

        // Stock and totals are the restored ledger plus the new sale: the lost period (s4, s5) is recorded,
        // not invented; s3 is in the backup.
        Assert.Equal(3L, pc.Count("Orders", $"Id IN ('{s1.OrderId}', '{s2.OrderId}', '{s3.OrderId}')"));
        Assert.Equal(0L, pc.Count("Orders", $"Id IN ('{s4.OrderId}', '{s5.OrderId}')"));
        Assert.Equal(1L, pc.Count("AuditLogs", "Action = 'Recovery.RestoredOnNewDevice'"));
    }

    [Fact]
    public async Task A_held_upload_whose_destination_copy_differs_stays_held()
    {
        var lost = _lost;
        Ok(await lost.Sync.SetSimulatedDestinationAsync(lost.Owner, true));
        var sale = await lost.SellCashAsync((lost.Tea, 1));
        var backup = Ok(await lost.Backups.CreateBackupAsync(lost.Owner));

        // Something else already sits on the destination under this sale's logical key.
        lost.Remote.Files["foreign"] = ("transactions", "x.json", [1, 2, 3],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["logicalKey"] = Key(sale), ["sha256"] = new string('0', 64) });

        var pc = _replacement = SalesHarness.CreateBlankDevice(lost.Remote, lost.Clock.UtcNowMilliseconds + 3_600_000);
        Ok(await pc.Backups.RestoreOnNewDeviceAsync(backup.PackagePath, OwnerUsername, OwnerPassword, "drill"));
        await pc.SignInOwnerAsync();
        await pc.Sync.RunCycleAsync();
        var release = Ok(await pc.Sync.ReleaseHeldUploadsAsync(pc.Owner, "checked"));
        Assert.True(release.StillHeld >= 1);
        Assert.Equal(1L, pc.Count("SyncQueue", $"State = 'RecoveryHold' AND LastErrorCode = '{SyncService.Codes.RemoteConflict}'"));
    }

    [Fact]
    public async Task New_pc_restore_needs_an_account_that_may_restore()
    {
        var lost = _lost;
        var (_, _) = await lost.Security.CreateUserAsync(lost.Owner, "cashier.one", Application.Security.RoleTemplates.Cashier);
        var backup = Ok(await lost.Backups.CreateBackupAsync(lost.Owner));

        var pc = _replacement = SalesHarness.CreateBlankDevice(lost.Remote, lost.Clock.UtcNowMilliseconds + 60_000);
        Assert.Equal(ResultStatus.Unauthenticated, (await pc.Backups.RestoreOnNewDeviceAsync(backup.PackagePath, "cashier.one", UserPassword, "drill")).Status);
        Assert.Equal(ResultStatus.Unauthenticated, (await pc.Backups.RestoreOnNewDeviceAsync(backup.PackagePath, "nobody", OwnerPassword, "drill")).Status);
        Assert.Equal(InstallationMode.Normal.ToString(), pc.Security.Database.Scalar<string>("SELECT Mode FROM InstallationState"));
    }
}
