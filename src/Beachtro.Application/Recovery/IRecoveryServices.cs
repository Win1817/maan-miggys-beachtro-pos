using Beachtro.Application.Common;
using Beachtro.Application.Security;

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

    Task<Result<Unit>> ReopenAsync(UserSession session, string notes, CancellationToken cancellationToken = default);
}
