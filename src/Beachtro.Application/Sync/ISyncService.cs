using Beachtro.Application.Common;
using Beachtro.Application.Security;

namespace Beachtro.Application.Sync;

/// <summary>An artifact or upload that needs attention.</summary>
public sealed record SyncProblem(string LogicalKey, string State, string Code, string? Detail, long SinceUtc);

/// <summary>Local backup health (sync design §Scheduling and backup package).</summary>
public sealed record BackupHealth(
    long? LastVerifiedUtc,
    string? LastBackupFile,
    int LocalGenerations,
    long FreeBytes,
    long WarningThresholdBytes,
    bool LowSpace);

public sealed record ScheduledJobView(string Kind, string? LastCompletedKey, string? WaitingKey, string State, string? LastError);

public sealed record SyncStatus(
    bool DestinationEnabled,
    string? Provider,
    string? DestinationLabel,
    int AwaitingFile,
    int HeldArtifacts,
    int Pending,
    int InProgress,
    int Failed,
    int RecoveryHold,
    int Succeeded,
    int OtherDestination,
    long? OldestPendingUtc,
    long? LastSuccessUtc,
    IReadOnlyList<SyncProblem> Problems,
    BackupHealth Backup,
    IReadOnlyList<ScheduledJobView> Jobs);

public sealed record SyncCycleResult(int Materialized, int Held, int Enqueued, int Uploaded, int Retrying, int Failed, int RecoveryHeld)
{
    public static SyncCycleResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Artifact files and the durable upload queue (FR-026, FR-027; IB-19, IB-20). Checkout never waits
/// for any of this: files and uploads happen after commit, in the background.
/// </summary>
/// <summary>Outcome of releasing held uploads.</summary>
public sealed record HoldRelease(int Adopted, int Released, int StillHeld);

public interface ISyncService
{
    Task<Result<SyncStatus>> GetStatusAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>Test profile only: binds or toggles the simulated destination folder (no Google account).</summary>
    Task<Result<Unit>> SetSimulatedDestinationAsync(UserSession session, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Failed uploads go back to Pending now, keeping their remote identity and content.</summary>
    Task<Result<int>> RetryFailedAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery (POS-DEC-010): for each upload on hold, searches the destination by logical key. A copy with
    /// the same checksum is adopted; a copy with other content stays held; with none found the file is
    /// released for a normal upload. Needs sync.configure, fresh reauthentication, an enabled destination
    /// and evidence text (what was checked).
    /// </summary>
    Task<Result<HoldRelease>> ReleaseHeldUploadsAsync(UserSession session, string evidence, CancellationToken cancellationToken = default);

    /// <summary>One bounded background cycle: write pending files, queue them, upload what is due.</summary>
    Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process scheduler (sync design §Scheduling): daily backup and monthly report catch-up. Jobs run
/// as the signed-in user when that user holds the needed capability; otherwise they wait.
/// </summary>
public interface IJobScheduler
{
    Task<IReadOnlyList<string>> RunDueAsync(UserSession? session, CancellationToken cancellationToken = default);
}
