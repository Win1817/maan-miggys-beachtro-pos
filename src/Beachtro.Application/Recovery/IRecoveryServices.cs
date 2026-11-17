using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Recovery;

/// <summary>A verified backup package (one .zip: database snapshot, journals and manifest). Sha256 is over the whole package.</summary>
public sealed record BackupInfo(Guid BackupId, string PackagePath, long SizeBytes, string Sha256, long CreatedUtc, string? LastSaleNumber, int Pruned);

public sealed record BackupCandidateReport(
    bool IsRestorable,
    IReadOnlyList<string> Problems,
    Guid? BackupId,
    Guid? InstallationId,
    string? LastSaleNumber,
    long CreatedUtc);

public sealed record SeriesHold(string SeriesKey, int LastReservedSequence, string Reason);

public sealed record NumberReconciliation(IReadOnlyList<SeriesHold> Blocked, bool JournalRepaired);

public sealed record RestoreResult(Guid RecoveryRunId, string PreservedFolder, IReadOnlyList<SeriesHold> BlockedSeries, int UnresolvedPreparedRequests);

/// <summary>
/// Number series safety (finance design §Number allocation; IB-08). Reconciles the database with
/// the external journal and blocks any series whose high-water mark cannot be proven.
/// </summary>
public interface INumberSeriesService
{
    Task<NumberReconciliation> ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>Owner/recovery action: reopen a blocked series above an evidenced high-water mark.</summary>
    Task<Result<Unit>> ResolveHoldAsync(UserSession session, string seriesKey, int provenHighWater, string evidence, CancellationToken cancellationToken = default);
}

/// <summary>Consistent local backup, candidate validation and controlled restore (FR-028; IB-12).</summary>
public interface IBackupService
{
    Task<Result<BackupInfo>> CreateBackupAsync(UserSession session, CancellationToken cancellationToken = default);

    Task<Result<BackupCandidateReport>> ValidateCandidateAsync(UserSession session, string backupFolder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Preserves the current database as evidence, replaces it with a validated candidate and leaves the
    /// installation in RecoveryRequired. Selling stays blocked until <see cref="ReopenAsync"/>.
    /// </summary>
    Task<Result<RestoreResult>> RestoreAsync(UserSession session, string backupFolder, string reason, CancellationToken cancellationToken = default);

    /// <summary>Refused while any number series is on hold or any prepared checkout/refund is unresolved.</summary>
    Task<Result<Unit>> ReopenAsync(UserSession session, string notes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replacement PC (POS-DEC-010): allowed only on a brand-new installation (no owner, no sales). The
    /// credentials must belong to an active account in the backup that may restore backups. The backup's
    /// installation identity is kept; every series that may have been used on the lost PC is blocked.
    /// </summary>
    Task<Result<RestoreResult>> RestoreOnNewDeviceAsync(string packagePath, string username, string password, string reason, CancellationToken cancellationToken = default);
}

/// <summary>A checkout or refund prepared but never completed in the restored data.</summary>
public sealed record PreparedRequestView(Guid RequestId, bool IsRefund, long PreparedUtc, string ActorName, long? TotalMinor, bool HasPaymentEvidence);

public sealed record RecoveryRunView(Guid RunId, long StartedUtc, long? FinishedUtc, string? LastSaleNumber, string PreservedPath, string Disposition, string? Notes);

public sealed record RecoveryStatus(
    InstallationMode Mode,
    RecoveryRunView? LatestRun,
    IReadOnlyList<SeriesHold> BlockedSeries,
    IReadOnlyList<PreparedRequestView> PreparedRequests,
    int HeldUploads,
    IReadOnlyList<string> ReopenBlockers)
{
    public bool InRecovery => Mode != InstallationMode.Normal;
}

/// <summary>The Recovery page's view of the runbook (IB-24): what still blocks reopening, and closing unfinished payments.</summary>
public interface IRecoveryService
{
    /// <summary>Needs backup.restore.</summary>
    Task<Result<RecoveryStatus>> GetStatusAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a prepared checkout or refund during recovery. It is never completed here, because it may
    /// already have been completed on the lost PC; the reason records what really happened.
    /// </summary>
    Task<Result<Unit>> ClosePreparedRequestAsync(UserSession session, Guid requestId, string reason, CancellationToken cancellationToken = default);
}
