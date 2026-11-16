using System.Text;
using Beachtro.Application.Common;
using Beachtro.Application.Recovery;
using Beachtro.Application.Security;
using Beachtro.Infrastructure.Numbering;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>T-07 numbering safety and T-16 backup/restore: no number is ever reused.</summary>
public sealed class RecoveryTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private string JournalPath => _h.Security.Database.Paths.NumberJournalFile;

    private async Task<UserSession> SignInOwnerAgainAsync() => await _h.Security.SignInAsync(OwnerUsername, OwnerPassword);

    [Fact]
    public async Task A_reserved_number_whose_sale_never_committed_is_skipped_not_reused()
    {
        await _h.SellCashAsync((_h.Tea, 1));

        // Simulate a reservation whose database transaction failed after the journal flush.
        await using (var db = _h.Security.Database.NewContext())
        {
            Ok(await _h.Numbers.ReserveAsync(db, "2025-11-16", Guid.NewGuid(), CancellationToken.None));
        }

        var next = await _h.SellCashAsync((_h.Tea, 1));
        Assert.Equal("MB-20251116-000003", next.TransactionNumber);
    }

    [Fact]
    public async Task A_torn_final_journal_line_is_repaired_with_evidence_preserved()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        await File.AppendAllTextAsync(JournalPath, "v1|torn-write-without-newline", Encoding.UTF8);

        // Until reconciliation runs, no number can be reserved after a torn write.
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));
        Assert.Equal(ResultStatus.RecoveryRequired, (await _h.Checkout.CompleteAsync(_h.Owner, requestId, new Application.Sales.CashPayment(100_00))).Status);

        var reconciliation = await _h.Numbers.ReconcileAsync();
        Assert.True(reconciliation.JournalRepaired);
        Assert.Empty(reconciliation.Blocked);
        Assert.NotEmpty(Directory.GetFiles(_h.Security.Database.Paths.Recovery, "numbers.journal.*.evidence"));

        Assert.Equal("MB-20251116-000002", (await _h.SellCashAsync((_h.Tea, 1))).TransactionNumber);
    }

    [Fact]
    public async Task A_damaged_journal_blocks_the_series_until_the_owner_proves_a_high_water_mark()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        await _h.SellCashAsync((_h.Tea, 1));
        var lines = (await File.ReadAllLinesAsync(JournalPath)).ToList();
        lines[0] = lines[0].Replace("|1|", "|7|", StringComparison.Ordinal); // breaks the hash chain
        await File.WriteAllTextAsync(JournalPath, string.Join('\n', lines) + "\n");

        var reconciliation = await _h.Numbers.ReconcileAsync();
        Assert.Equal(["MB-20251116"], reconciliation.Blocked.Select(b => b.SeriesKey));

        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));
        Assert.Equal(ResultStatus.RecoveryRequired, (await _h.Checkout.CompleteAsync(_h.Owner, requestId, new Application.Sales.CashPayment(100_00))).Status);

        Assert.Equal(ResultStatus.Validation, (await _h.Numbers.ResolveHoldAsync(_h.Owner, "MB-20251116", 1, "printed receipts reviewed")).Status);
        Ok(await _h.Numbers.ResolveHoldAsync(_h.Owner, "MB-20251116", 5, "printed receipts reviewed up to 000005"));

        Assert.True(_h.Numbers.Journal.Read().Trustworthy);
        Assert.Equal("MB-20251116-000006", Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new Application.Sales.CashPayment(100_00))).TransactionNumber);
    }

    [Fact]
    public async Task A_missing_journal_blocks_existing_series()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        File.Delete(JournalPath);

        var reconciliation = await _h.Numbers.ReconcileAsync();
        Assert.Single(reconciliation.Blocked);
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Numbering.SeriesBlocked'"));
    }

    [Fact]
    public async Task Backups_are_consistent_verified_and_checksummed()
    {
        await _h.SellCashAsync((_h.Tea, 2));
        var backup = Ok(await _h.Backups.CreateBackupAsync(_h.Owner));

        Assert.True(File.Exists(Path.Combine(backup.Folder, "pos.db")));
        Assert.True(File.Exists(Path.Combine(backup.Folder, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(backup.Folder, "numbers.journal")));
        Assert.Equal("MB-20251116-000001", backup.LastSaleNumber);
        Assert.Empty(Directory.GetDirectories(_h.Security.Database.Paths.Backups, "*.partial"));
        Assert.Equal(1L, _h.Count("Backups", $"Id = '{backup.BackupId}' AND VerificationState = 'Verified' AND Sha256 = '{backup.Sha256}'"));

        var report = Ok(await _h.Backups.ValidateCandidateAsync(_h.Owner, backup.Folder));
        Assert.True(report.IsRestorable, string.Join(" ", report.Problems));
    }

    [Fact]
    public async Task A_tampered_backup_is_rejected()
    {
        var backup = Ok(await _h.Backups.CreateBackupAsync(_h.Owner));
        await using (var stream = new FileStream(Path.Combine(backup.Folder, "pos.db"), FileMode.Open, FileAccess.Write))
        {
            stream.Seek(-1, SeekOrigin.End);
            stream.WriteByte(0x42);
        }

        var report = Ok(await _h.Backups.ValidateCandidateAsync(_h.Owner, backup.Folder));
        Assert.False(report.IsRestorable);
        Assert.Contains(report.Problems, p => p.Contains("checksum", StringComparison.Ordinal));
        Assert.Equal(ResultStatus.Validation, (await _h.Backups.RestoreAsync(_h.Owner, backup.Folder, "drill")).Status);
    }

    [Fact]
    public async Task Restoring_an_old_backup_preserves_evidence_pauses_selling_and_never_reuses_numbers()
    {
        await _h.SellCashAsync((_h.Tea, 1));                       // 000001, inside the backup
        var backup = Ok(await _h.Backups.CreateBackupAsync(_h.Owner));
        await _h.SellCashAsync((_h.Tea, 1));                       // 000002, after the backup
        await _h.SellCashAsync((_h.Tea, 1));                       // 000003, after the backup

        var restored = Ok(await _h.Backups.RestoreAsync(_h.Owner, backup.Folder, "drill: replacement after disk fault"));
        Assert.True(File.Exists(Path.Combine(restored.PreservedFolder, "pos.db")));
        Assert.True(File.Exists(Path.Combine(restored.PreservedFolder, "numbers.journal")));
        Assert.Empty(restored.BlockedSeries);

        // The restored ledger has only the first sale; its series is raised to the journal high-water mark.
        Assert.Equal(1L, _h.Count("Orders"));
        Assert.Equal(3L, _h.Security.Database.Scalar<long>("SELECT LastReservedSequence FROM NumberSeries WHERE SeriesKey = 'MB-20251116'"));
        Assert.Equal("RecoveryRequired", _h.Security.Database.Scalar<string>("SELECT Mode FROM InstallationState"));
        Assert.Equal(1L, _h.Count("RecoveryRuns", "Disposition = 'Hold'"));

        // The old session no longer matches the restored database; the owner signs in again.
        var owner = await SignInOwnerAgainAsync();
        Assert.Equal(ResultStatus.RecoveryRequired, (await _h.Checkout.PrepareAsync(owner, Guid.NewGuid(), SalesHarness.Cart((_h.Tea, 1)))).Status);

        Ok(await _h.Backups.ReopenAsync(owner, "lost sales 000002-000003 recorded from printed receipts"));
        var next = await _h.SellAsync(owner, SalesHarness.Cart((_h.Tea, 1)), new Application.Sales.CashPayment(100_00));
        Assert.Equal("MB-20251116-000004", next.TransactionNumber);
        Assert.Equal(1L, _h.Count("RecoveryRuns", "Disposition = 'Completed'"));
    }

    [Fact]
    public async Task Reopening_is_refused_while_any_series_is_on_hold()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        var backup = Ok(await _h.Backups.CreateBackupAsync(_h.Owner));
        File.Delete(JournalPath);

        var restored = Ok(await _h.Backups.RestoreAsync(_h.Owner, backup.Folder, "drill: journal lost with disk"));
        Assert.Single(restored.BlockedSeries);

        var owner = await SignInOwnerAgainAsync();
        Assert.Equal(ResultStatus.Conflict, (await _h.Backups.ReopenAsync(owner, "try")).Status);

        Ok(await _h.Numbers.ResolveHoldAsync(owner, "MB-20251116", 10, "Drive copies and printed receipts reviewed up to 000010"));
        Ok(await _h.Backups.ReopenAsync(owner, "series resolved"));
        Assert.Equal("MB-20251116-000011", (await _h.SellAsync(owner, SalesHarness.Cart((_h.Tea, 1)), new Application.Sales.CashPayment(100_00))).TransactionNumber);
    }

    [Fact]
    public async Task Cashiers_cannot_back_up_or_restore()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Backups.CreateBackupAsync(cashier)).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Backups.RestoreAsync(cashier, "x", "x")).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Numbers.ResolveHoldAsync(cashier, "MB-20251116", 9, "x")).Status);
    }

    [Fact]
    public void Journal_records_are_hash_chained()
    {
        var journal = new NumberJournal(Path.Combine(_h.Security.Database.Paths.Data, "chain-test.journal"));
        var installation = Guid.NewGuid();
        var r1 = journal.Append(journal.Read(), installation, "MB-20251116", 1, Guid.NewGuid(), 1);
        var r2 = journal.Append(journal.Read(), installation, "MB-20251116", 2, Guid.NewGuid(), 2);

        Assert.Equal(NumberJournal.Genesis, r1.PreviousHash);
        Assert.Equal(r1.Hash, r2.PreviousHash);
        var state = journal.Read();
        Assert.True(state.Trustworthy);
        Assert.Equal(2, state.HighWater("MB-20251116"));
    }
}
