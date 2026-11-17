using System.Text;
using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Infrastructure.Sync;
using Beachtro.Infrastructure.Tests.Sales;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sync;

/// <summary>T-14 materialization and T-15 remote deduplication/leases, against an in-memory fake remote.</summary>
public sealed class SyncTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Root => _h.Security.Database.Paths.Root;

    private Guid Binding => Guid.Parse(_h.Security.Database.Scalar<string>("SELECT BindingId FROM CloudStorageSettings"));

    private async Task EnableAsync() => Ok(await _h.Sync.SetSimulatedDestinationAsync(_h.Owner, true));

    private string QueueState(Guid orderId) => _h.Security.Database.Scalar<string>(
        $"SELECT q.State FROM SyncQueue q JOIN ArtifactRecords a ON a.Id = q.ArtifactId WHERE a.OrderId = '{orderId}'");

    private string? RemoteId(Guid orderId) => _h.Security.Database.Column(
        $"SELECT q.RemoteFileId FROM SyncQueue q JOIN ArtifactRecords a ON a.Id = q.ArtifactId WHERE a.OrderId = '{orderId}' AND q.RemoteFileId IS NOT NULL").SingleOrDefault();

    [Fact]
    public async Task A_sale_file_is_written_from_its_snapshot_and_uploaded_once()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();

        var cycle = await _h.Sync.RunCycleAsync();
        Assert.Equal(1, cycle.Materialized);
        Assert.Equal(1, cycle.Uploaded);

        var file = Path.Combine(Root, "artifacts", "transactions", "2025", "11", $"sale_{sale.OrderId:D}_v1.json");
        var snapshot = _h.Security.Database.Scalar<string>($"SELECT SnapshotJson FROM Orders WHERE Id = '{sale.OrderId}'");
        Assert.Equal(Encoding.UTF8.GetBytes(snapshot), await File.ReadAllBytesAsync(file));

        Assert.Equal("Succeeded", QueueState(sale.OrderId));
        var remote = Assert.Single(_h.Remote.Files);
        Assert.Equal(RemoteId(sale.OrderId), remote.Key);
        Assert.Equal("Transactions/2025/11", remote.Value.Folder);
        Assert.Equal($"sale/{sale.OrderId:D}/schema1", remote.Value.Properties["logicalKey"]);
        Assert.Single(_h.Sync.Journal.Read().Records);

        var again = await _h.Sync.RunCycleAsync();
        Assert.Equal(0, again.Uploaded);
        Assert.Equal(1, _h.Remote.CreateCalls);
    }

    [Fact]
    public async Task A_lost_acknowledgement_never_creates_a_second_file()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();

        // The create lands, the response is lost, and the read-back does not see it yet.
        _h.Remote.FailAfterCreate = new RemoteStoreException(RemoteErrorKind.Transient, "timeout", 504);
        _h.Remote.HiddenReads = 1;
        var first = await _h.Sync.RunCycleAsync();
        Assert.Equal(1, first.Retrying);
        var id = RemoteId(sale.OrderId);
        Assert.NotNull(id);

        _h.Clock.Advance(TimeSpan.FromHours(2));
        var second = await _h.Sync.RunCycleAsync();
        Assert.Equal(1, second.Uploaded);
        Assert.Equal(1, _h.Remote.CreateCalls);                   // the retry read the same ID back instead of creating again
        Assert.Equal(id, Assert.Single(_h.Remote.Files).Key);
        Assert.Equal(2L, _h.Count("SyncHistory"));
    }

    [Fact]
    public async Task A_transient_failure_backs_off_honours_retry_after_and_keeps_history()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();
        _h.Remote.FailBeforeCreate = new RemoteStoreException(RemoteErrorKind.RateLimited, "slow down", 429, TimeSpan.FromMinutes(10));

        var first = await _h.Sync.RunCycleAsync();
        Assert.Equal(1, first.Retrying);
        Assert.Equal("Pending", QueueState(sale.OrderId));
        Assert.Equal("RateLimited", _h.Security.Database.Scalar<string>("SELECT LastErrorCode FROM SyncQueue"));
        Assert.True(_h.Security.Database.Scalar<long>("SELECT NextAttemptUtc FROM SyncQueue") >= _h.Clock.UtcNowMilliseconds + 600_000);

        Assert.Equal(0, (await _h.Sync.RunCycleAsync()).Uploaded);   // not due yet

        _h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).Uploaded);
        Assert.Equal(["Failed", "Succeeded"], _h.Security.Database.Column("SELECT Outcome FROM SyncHistory ORDER BY AttemptNo"));
        Assert.Equal(429L, _h.Security.Database.Scalar<long>("SELECT HttpCode FROM SyncHistory WHERE AttemptNo = 1"));
    }

    [Fact]
    public async Task An_authorization_failure_waits_for_a_manual_retry_that_keeps_the_same_identity()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();
        _h.Remote.FailBeforeCreate = new RemoteStoreException(RemoteErrorKind.Unauthorized, "token revoked", 401);

        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).Failed);
        Assert.Equal("Failed", QueueState(sale.OrderId));
        var id = RemoteId(sale.OrderId);

        _h.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, (await _h.Sync.RunCycleAsync()).Uploaded);   // failures are not retried automatically

        Assert.Equal(ResultStatus.Forbidden, (await _h.Sync.RetryFailedAsync(cashier)).Status);
        Assert.Equal(1, Ok(await _h.Sync.RetryFailedAsync(_h.Owner)));
        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).Uploaded);
        Assert.Equal(id, Assert.Single(_h.Remote.Files).Key);
    }

    [Fact]
    public async Task A_remote_file_with_other_content_under_our_id_is_held_not_overwritten()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();
        _h.Remote.FailBeforeCreate = new RemoteStoreException(RemoteErrorKind.Transient, "reset", null);
        await _h.Sync.RunCycleAsync();
        var id = RemoteId(sale.OrderId)!;

        _h.Remote.Files[id] = ("Elsewhere", "other.json", [1, 2, 3], new Dictionary<string, string> { ["logicalKey"] = "something/else" });
        _h.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).RecoveryHeld);

        Assert.Equal("RecoveryHold", QueueState(sale.OrderId));
        Assert.Equal("RemoteMismatch", _h.Security.Database.Scalar<string>("SELECT LastErrorCode FROM SyncQueue"));
        Assert.Equal([1, 2, 3], _h.Remote.Files[id].Bytes);
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Sync.Held'"));
    }

    [Fact]
    public async Task A_worker_whose_lease_expired_cannot_acknowledge_or_create_a_duplicate()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();
        await _h.Materializer.MaterializePendingAsync(10);
        await _h.Sync.EnqueueAsync(Binding, CancellationToken.None);

        var stale = await _h.Sync.ClaimNextAsync(Binding, CancellationToken.None);
        Assert.NotNull(stale);

        // The first worker stalls past its lease; a second worker reclaims and finishes the upload.
        _h.Clock.Advance(TimeSpan.FromMilliseconds(SyncService.LeaseMs + 1));
        using var other = _h.NewSyncWorker();
        Assert.Equal(1, (await other.RunCycleAsync()).Uploaded);

        Assert.Equal(SyncService.Outcome.LostLease, await _h.Sync.ProcessAsync(_h.Remote, stale!, CancellationToken.None));
        Assert.Single(_h.Remote.Files);
        Assert.Equal("Succeeded", QueueState(sale.OrderId));
        Assert.Equal(["Unknown", "Succeeded"], _h.Security.Database.Column("SELECT Outcome FROM SyncHistory ORDER BY AttemptNo"));
    }

    [Fact]
    public async Task Without_a_destination_files_are_still_written_but_nothing_is_queued()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        var cycle = await _h.Sync.RunCycleAsync();
        Assert.Equal(1, cycle.Materialized);
        Assert.Equal(0L, _h.Count("SyncQueue"));
        Assert.Equal(0, _h.Remote.CreateCalls);

        Ok(await _h.Sync.SetSimulatedDestinationAsync(_h.Owner, true));
        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).Uploaded);

        Ok(await _h.Sync.SetSimulatedDestinationAsync(_h.Owner, false));
        await _h.SellCashAsync((_h.Tea, 1));
        Assert.Equal(0, (await _h.Sync.RunCycleAsync()).Enqueued);
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Sync.DestinationDisabled'"));
    }

    [Fact]
    public async Task A_missing_upload_journal_holds_new_creates_instead_of_guessing()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();
        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).Uploaded);

        File.Delete(_h.Security.Database.Paths.UploadJournalFile);
        var second = await _h.SellCashAsync((_h.Tea, 1));
        Assert.Equal(1, (await _h.Sync.RunCycleAsync()).RecoveryHeld);
        Assert.Equal("RecoveryHold", QueueState(second.OrderId));
        Assert.Single(_h.Remote.Files);
    }

    [Fact]
    public async Task Restoring_an_older_database_reuses_journaled_identities_and_uploads_nothing_twice()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        var backup = Ok(await _h.Backups.CreateBackupAsync(_h.Owner));
        await _h.SellCashAsync((_h.Tea, 1));
        await EnableAsync();
        var uploaded = (await _h.Sync.RunCycleAsync()).Uploaded;
        Assert.Equal(3, uploaded);                                   // two sales and the backup package
        var before = _h.Remote.Files.Count;

        // The restored database knows nothing about those uploads; the live journal does.
        Ok(await _h.Backups.RestoreAsync(_h.Owner, backup.PackagePath, "drill: restore after upload"));
        var owner = await _h.Security.SignInAsync(OwnerUsername, OwnerPassword);
        Ok(await _h.Sync.SetSimulatedDestinationAsync(owner, true));

        // The restored sale is written again with identical bytes; its journaled identity is read back, not re-created.
        var after = await _h.Sync.RunCycleAsync();
        Assert.Equal(1, after.Uploaded);
        Assert.Equal(before, _h.Remote.Files.Count);
        Assert.Equal(3, _h.Remote.CreateCalls);
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Sync.DestinationEnabled' AND SafeDetailsJson LIKE '%\"bindingFromJournal\":true%'"));
    }

    [Fact]
    public void Backoff_uses_full_jitter_with_a_one_hour_cap_and_honours_retry_after()
    {
        Assert.Equal(10_000, SyncService.BackoffMs(1, null, 1.0));
        Assert.Equal(3_600_000, SyncService.BackoffMs(30, null, 1.0));
        Assert.Equal(0, SyncService.BackoffMs(3, null, 0.0));
        Assert.Equal(30_000, SyncService.BackoffMs(1, TimeSpan.FromSeconds(30), 0.0));
    }
}
