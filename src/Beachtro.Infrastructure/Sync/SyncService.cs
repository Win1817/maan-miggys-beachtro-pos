using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Runtime;
using Beachtro.Application.Security;
using Beachtro.Application.Sync;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sync;

/// <summary>
/// IB-20 durable upload queue (sync design §Queue state machine and leases, §Retry-safe remote
/// creation). Database writes are short and go through the write gate; no lock is held during a
/// remote call. The remote ID is journaled and stored before the first create, and every retry
/// reuses it, so a lost acknowledgement never produces a second file.
/// </summary>
public sealed class SyncService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IClock clock,
    WriteGate writeGate,
    LocalDataPaths paths,
    ArtifactMaterializer materializer,
    IRemoteStoreFactory stores,
    Func<double>? random = null) : ISyncService, IDisposable
{
    public const long LeaseMs = 120_000;
    public const int RenewIntervalMs = 30_000;
    public const int MaterializeBatch = 100;
    public const int UploadBatch = 25;
    public const int TransactionJobsPerBackup = 20;
    public const string SimulatedLabel = "Simulated destination (TEST, no Google account)";

    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;
    private int _transactionJobsSinceBackup;
    private bool _tempCleaned;

    /// <summary>Random per process start (sync design: lease owner).</summary>
    public string LeaseOwner { get; } = "w_" + Guid.NewGuid().ToString("N")[..12];

    public UploadJournal Journal { get; } = new(paths.UploadJournalFile);

    public static class Codes
    {
        public const string IdentityJournalDamaged = "IdentityJournalDamaged";
        public const string IdentityJournalMissing = "IdentityJournalMissing";

        /// <summary>Restored on a new PC: the backup's journal may predate uploads made on the lost PC (POS-DEC-010).</summary>
        public const string IdentityJournalStale = "IdentityJournalStale";
        public const string RemoteMismatch = "RemoteMismatch";
        public const string RemoteConflict = "RemoteConflict";
        public const string NotVisibleYet = "NotVisibleYet";
        public const string Transient = "Transient";
        public const string RateLimited = "RateLimited";
        public const string AuthorizationRequired = "AuthorizationRequired";
        public const string PermissionDenied = "PermissionDenied";
        public const string StorageQuotaExceeded = "StorageQuotaExceeded";
        public const string RemoteRejected = "RemoteRejected";
    }

    internal enum Outcome { Succeeded, Retrying, Failed, RecoveryHold, LostLease }

    internal sealed record Claim(Guid QueueId, Guid ArtifactId, Guid BindingId, int AttemptNo, string? RemoteFileId, string FolderKey);

    // ---------------------------------------------------------------- operator actions

    public async Task<Result<SyncStatus>> GetStatusAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.SyncView, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<SyncStatus>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await db.CloudStorageSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var binding = settings?.BindingId;

        var activeHolds = db.ArtifactHolds.Where(h => h.ResolvedUtc == null);
        var awaiting = await db.ArtifactRecords.CountAsync(a => a.State == ArtifactState.Intent && !activeHolds.Any(h => h.ArtifactId == a.Id), cancellationToken).ConfigureAwait(false);
        var held = await activeHolds.CountAsync(cancellationToken).ConfigureAwait(false);

        var counts = await db.SyncQueue.Where(q => q.AccountBindingId == binding)
            .GroupBy(q => q.State).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        var other = binding is null ? await db.SyncQueue.CountAsync(cancellationToken).ConfigureAwait(false)
            : await db.SyncQueue.CountAsync(q => q.AccountBindingId != binding, cancellationToken).ConfigureAwait(false);
        var oldest = await db.SyncQueue.Where(q => q.AccountBindingId == binding && (q.State == SyncState.Pending || q.State == SyncState.InProgress))
            .MinAsync(q => (long?)q.CreatedUtc, cancellationToken).ConfigureAwait(false);
        var lastSuccess = await db.SyncQueue.Where(q => q.State == SyncState.Succeeded).MaxAsync(q => q.CompletedUtc, cancellationToken).ConfigureAwait(false);

        var problems = new List<SyncProblem>();
        var queueProblems = await (
            from q in db.SyncQueue
            join a in db.ArtifactRecords on q.ArtifactId equals a.Id
            where q.State == SyncState.Failed || q.State == SyncState.RecoveryHold
            orderby q.CreatedUtc
            select new { a.LogicalKey, q.State, q.LastErrorCode, q.Id, q.CreatedUtc })
            .Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var p in queueProblems)
        {
            var last = await db.SyncHistory.Where(h => h.QueueId == p.Id).OrderByDescending(h => h.AttemptNo)
                .Select(h => new { h.SafeError, h.FinishedUtc }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            problems.Add(new SyncProblem(p.LogicalKey, p.State.ToString(), p.LastErrorCode ?? "Unknown", last?.SafeError, last?.FinishedUtc ?? p.CreatedUtc));
        }

        var holdProblems = await (
            from h in activeHolds
            join a in db.ArtifactRecords on h.ArtifactId equals a.Id
            orderby h.DetectedUtc
            select new SyncProblem(a.LogicalKey, "FileHeld", h.Code, h.Detail, h.DetectedUtc))
            .Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        problems.AddRange(holdProblems);

        var jobs = await db.ScheduledJobs.AsNoTracking().OrderBy(j => j.Kind).ToListAsync(cancellationToken).ConfigureAwait(false);
        var health = await BackupHealthAsync(db, paths, cancellationToken).ConfigureAwait(false);

        return Result<SyncStatus>.Ok(new SyncStatus(
            settings?.Enabled == true && binding is not null,
            settings?.Provider,
            settings?.AccountLabel,
            awaiting,
            held,
            counts.GetValueOrDefault(SyncState.Pending),
            counts.GetValueOrDefault(SyncState.InProgress),
            counts.GetValueOrDefault(SyncState.Failed),
            counts.GetValueOrDefault(SyncState.RecoveryHold),
            counts.GetValueOrDefault(SyncState.Succeeded),
            other,
            oldest,
            lastSuccess,
            problems,
            health,
            jobs.Select(j => new ScheduledJobView(j.Kind.ToString(), j.LastCompletedKey,
                j.LastDueKey is not null && j.LastDueKey != j.LastCompletedKey ? j.LastDueKey : null, j.State.ToString(), j.LastError)).ToList()));
    }

    public async Task<Result<Unit>> SetSimulatedDestinationAsync(UserSession session, bool enabled, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.SyncConfigure, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        if (paths.Profile != RuntimeProfile.Test)
        {
            return Result.Fail(ResultStatus.Validation, "The simulated destination exists only in the Test profile.");
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var settings = await db.CloudStorageSettings.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var adopted = false;
        if (settings is null)
        {
            // After a restore the settings row can be older than the uploads. The live upload journal
            // still names the binding those files went to; keep it so nothing is replayed as new files.
            var journal = Journal.Read();
            var binding = journal is { Trustworthy: true, Records.Count: > 0 } ? journal.Records[^1].BindingId : Guid.NewGuid();
            adopted = journal.Records.Count > 0;
            settings = new CloudStorageSettings
            {
                BindingId = binding,
                Provider = RemoteProviders.SimulatedFolder,
                AccountLabel = SimulatedLabel,
                RootFolderId = "simulated-drive",
                Enabled = enabled,
                LastConnectedUtc = enabled ? now : null,
            };
            db.CloudStorageSettings.Add(settings);
        }
        else if (settings.Provider != RemoteProviders.SimulatedFolder)
        {
            return Result.Fail(ResultStatus.Conflict, "Another destination is bound. Changing destination needs a reviewed rebinding; old files are never replayed automatically.");
        }
        else
        {
            settings.BindingId ??= Guid.NewGuid();
            settings.Enabled = enabled;
            settings.LastConnectedUtc = enabled ? now : settings.LastConnectedUtc;
            settings.Version++;
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, enabled ? "Sync.DestinationEnabled" : "Sync.DestinationDisabled",
            "CloudStorageSettings", settings.BindingId!.Value.ToString(), null, new { provider = settings.Provider, bindingFromJournal = adopted });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<Result<int>> RetryFailedAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.SyncRetry, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<int>();
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var failed = await db.SyncQueue.Where(q => q.State == SyncState.Failed).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var q in failed)
        {
            // Identity and content are kept; only the schedule and the actionable error change.
            q.State = SyncState.Pending;
            q.NextAttemptUtc = now;
            q.LastErrorCode = null;
            q.Version++;
        }

        var backupArtifacts = failed.Select(q => q.ArtifactId).ToList();
        foreach (var backup in await (from a in db.ArtifactRecords
                                      join b in db.Backups on a.BackupId equals b.Id
                                      where backupArtifacts.Contains(a.Id)
                                      select b).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            backup.UploadState = BackupUploadState.Pending;
        }

        if (failed.Count > 0)
        {
            AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Sync.Retried", "SyncQueue", null, null, new { count = failed.Count });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<int>.Ok(failed.Count);
    }

    public async Task<Result<HoldRelease>> ReleaseHeldUploadsAsync(UserSession session, string evidence, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.SyncConfigure, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<HoldRelease>();
        }

        var invalid = TextRules.ValidateRequired(evidence, "Evidence", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result<HoldRelease>.Fail(ResultStatus.Validation, invalid);
        }

        CloudStorageSettings? settings;
        List<(Guid QueueId, int Version, string LogicalKey, string? Sha256)> held;
        await using (var read = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            settings = await read.CloudStorageSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (settings is not { Enabled: true, BindingId: not null })
            {
                return Result<HoldRelease>.Fail(ResultStatus.Conflict, "Turn the destination on first, so existing copies can be found before anything is uploaded again.");
            }

            var binding = settings.BindingId.Value;
            held = (await (from q in read.SyncQueue
                           join a in read.ArtifactRecords on q.ArtifactId equals a.Id
                           where q.State == SyncState.RecoveryHold && q.AccountBindingId == binding
                           select new { q.Id, q.Version, a.LogicalKey, a.Sha256 })
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => (r.Id, r.Version, r.LogicalKey, (string?)r.Sha256)).ToList();
        }

        var store = stores.Create(settings);
        if (store is null)
        {
            return Result<HoldRelease>.Fail(ResultStatus.Conflict, "The destination is not available in this build.");
        }

        // Search outside the write gate (network); apply under it with version checks.
        var decisions = new List<(Guid QueueId, int Version, string? AdoptId, bool Release)>();
        foreach (var h in held)
        {
            IReadOnlyList<RemoteObject> copies;
            try
            {
                copies = await store.FindAsync(h.LogicalKey, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteStoreException ex)
            {
                return Result<HoldRelease>.Fail(ResultStatus.StorageUnavailable, "The destination could not be searched: " + ex.Message + " Nothing was changed.");
            }

            var same = copies.FirstOrDefault(c => h.Sha256 is not null && c.ContentSha256 == h.Sha256 && c.Properties.GetValueOrDefault("sha256") == h.Sha256);
            string? adoptId = same?.Id;
            decisions.Add((h.QueueId, h.Version, adoptId, same is null && copies.Count == 0));
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        int adopted = 0, released = 0, still = 0;
        foreach (var d in decisions)
        {
            var q = await db.SyncQueue.SingleOrDefaultAsync(x => x.Id == d.QueueId, cancellationToken).ConfigureAwait(false);
            if (q is null || q.Version != d.Version || q.State != SyncState.RecoveryHold)
            {
                continue;
            }

            if (d.AdoptId is not null || d.Release)
            {
                // An adopted ID is read back by the next attempt (identity, size, checksum) before it counts as done.
                q.RemoteFileId = d.AdoptId ?? q.RemoteFileId;
                q.State = SyncState.Pending;
                q.NextAttemptUtc = now;
                q.LastErrorCode = null;
                q.Version++;
                if (d.AdoptId is not null)
                {
                    adopted++;
                }
                else
                {
                    released++;
                }
            }
            else
            {
                q.LastErrorCode = Codes.RemoteConflict;
                q.Version++;
                still++;
            }
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Recovery.HeldUploadsReleased", "SyncQueue", null, evidence,
            new { adopted, released, stillHeld = still });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<HoldRelease>.Ok(new HoldRelease(adopted, released, still));
    }

    // ---------------------------------------------------------------- background cycle

    public async Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        // Cycles never overlap; a manual run waits for a background run in progress, then finds what is left.
        await _cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tempCleaned)
            {
                materializer.CleanupTempFiles();
                _tempCleaned = true;
            }

            var (ready, held) = await materializer.MaterializePendingAsync(MaterializeBatch, cancellationToken).ConfigureAwait(false);

            CloudStorageSettings? settings;
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                settings = await db.CloudStorageSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            var store = settings is { Enabled: true, BindingId: not null } ? stores.Create(settings) : null;
            if (store is null)
            {
                return new SyncCycleResult(ready, held, 0, 0, 0, 0, 0);
            }

            var binding = settings!.BindingId!.Value;
            var enqueued = await EnqueueAsync(binding, cancellationToken).ConfigureAwait(false);
            int uploaded = 0, retrying = 0, failed = 0, holds = 0;
            for (var i = 0; i < UploadBatch; i++)
            {
                var claim = await ClaimNextAsync(binding, cancellationToken).ConfigureAwait(false);
                if (claim is null)
                {
                    break;
                }

                switch (await ProcessAsync(store, claim, cancellationToken).ConfigureAwait(false))
                {
                    case Outcome.Succeeded: uploaded++; break;
                    case Outcome.Retrying: retrying++; break;
                    case Outcome.Failed: failed++; break;
                    case Outcome.RecoveryHold: holds++; break;
                    default: break;
                }
            }

            return new SyncCycleResult(ready, held, enqueued, uploaded, retrying, failed, holds);
        }
        finally
        {
            _cycle.Release();
        }
    }

    /// <summary>Queues every Ready artifact that has no queue row yet, for the current binding only.</summary>
    internal async Task<int> EnqueueAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var missing = await db.ArtifactRecords.AsNoTracking()
            .Where(a => a.State == ArtifactState.Ready && !db.SyncQueue.Any(q => q.ArtifactId == a.Id))
            .OrderBy(a => a.CreatedUtc).Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // After a replacement-PC restore, files that existed then may already be on the destination.
        var cutoff = await db.RecoveryRuns.MaxAsync(r => r.UploadIdentityCutoffUtc, cancellationToken).ConfigureAwait(false);
        var added = 0;
        foreach (var artifact in missing)
        {
            var location = await ArtifactMaterializer.LocateAsync(db, artifact, cancellationToken).ConfigureAwait(false);
            if (location is null || (artifact.Type == ArtifactType.Backup && !Exists(paths, location.RelativePath)))
            {
                // A backup pruned by retention before any destination existed is not queued.
                continue;
            }

            db.SyncQueue.Add(new SyncQueueItem
            {
                Id = Guid.NewGuid(),
                ArtifactId = artifact.Id,
                DestinationFolderKey = location.FolderKey,
                AccountBindingId = bindingId,
                State = artifact.CreatedUtc < cutoff ? SyncState.RecoveryHold : SyncState.Pending,
                LastErrorCode = artifact.CreatedUtc < cutoff ? Codes.IdentityJournalStale : null,
                NextAttemptUtc = now,
                CreatedUtc = now,
            });
            if (artifact.BackupId is { } backupId)
            {
                var backup = await db.Backups.SingleAsync(b => b.Id == backupId, cancellationToken).ConfigureAwait(false);
                backup.UploadState = BackupUploadState.Pending;
            }

            added++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <summary>
    /// Atomically claims the next due item (or one whose lease expired). Financial JSON first, then
    /// reports, then backups, but a waiting backup goes next after every 20 transaction jobs.
    /// </summary>
    internal async Task<Claim?> ClaimNextAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;

        var due =
            from q in db.SyncQueue
            join a in db.ArtifactRecords on q.ArtifactId equals a.Id
            where q.AccountBindingId == bindingId && a.State == ArtifactState.Ready
                && ((q.State == SyncState.Pending && q.NextAttemptUtc <= now) || (q.State == SyncState.InProgress && q.LeaseExpiresUtc < now))
            orderby q.NextAttemptUtc, q.CreatedUtc, q.Id
            select new { q.Id, q.Version, q.AttemptCount, q.RemoteFileId, q.DestinationFolderKey, a.Type, ArtifactId = a.Id };

        var transaction = await due.FirstOrDefaultAsync(x => x.Type == ArtifactType.SaleJson || x.Type == ArtifactType.CorrectionJson, cancellationToken).ConfigureAwait(false);
        var report = await due.FirstOrDefaultAsync(x => x.Type == ArtifactType.ReportCsv || x.Type == ArtifactType.ReportPdf, cancellationToken).ConfigureAwait(false);
        var backup = await due.FirstOrDefaultAsync(x => x.Type == ArtifactType.Backup, cancellationToken).ConfigureAwait(false);
        var pick = backup is not null && _transactionJobsSinceBackup >= TransactionJobsPerBackup ? backup : transaction ?? report ?? backup;
        if (pick is null)
        {
            return null;
        }

        _transactionJobsSinceBackup = pick.Type == ArtifactType.Backup ? 0 : _transactionJobsSinceBackup + 1;
        var attemptNo = pick.AttemptCount + 1;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.SyncQueue.Where(q => q.Id == pick.Id && q.Version == pick.Version)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.State, SyncState.InProgress)
                .SetProperty(q => q.LeaseOwner, LeaseOwner)
                .SetProperty(q => q.LeaseExpiresUtc, now + LeaseMs)
                .SetProperty(q => q.AttemptCount, attemptNo)
                .SetProperty(q => q.Version, pick.Version + 1), cancellationToken).ConfigureAwait(false);
        if (rows != 1)
        {
            return null;
        }

        // An attempt abandoned by an expired lease is closed as Unknown; its identity is kept.
        await db.SyncHistory.Where(h => h.QueueId == pick.Id && h.FinishedUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.FinishedUtc, now)
                .SetProperty(h => h.Outcome, SyncOutcome.Unknown)
                .SetProperty(h => h.SafeError, "Lease expired before the attempt finished."), cancellationToken).ConfigureAwait(false);
        db.SyncHistory.Add(new SyncAttempt
        {
            Id = Guid.NewGuid(),
            QueueId = pick.Id,
            AttemptNo = attemptNo,
            StartedUtc = now,
            RemoteFileId = pick.RemoteFileId,
            CorrelationId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Claim(pick.Id, pick.ArtifactId, bindingId, attemptNo, pick.RemoteFileId, pick.DestinationFolderKey);
    }

    internal async Task<Outcome> ProcessAsync(IRemoteStore store, Claim claim, CancellationToken cancellationToken)
    {
        ArtifactRecord artifact;
        Guid installationId;
        bool anyRemoteIdentity;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            artifact = await db.ArtifactRecords.AsNoTracking().SingleAsync(a => a.Id == claim.ArtifactId, cancellationToken).ConfigureAwait(false);
            installationId = await db.InstallationState.Select(s => s.InstallationId).SingleAsync(cancellationToken).ConfigureAwait(false);
            anyRemoteIdentity = await db.SyncQueue.AnyAsync(q => q.RemoteFileId != null, cancellationToken).ConfigureAwait(false);
        }

        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewTask = RenewLeaseAsync(claim.QueueId, renewal.Token);
        try
        {
            var bytes = await materializer.ReadReadyAsync(artifact, cancellationToken).ConfigureAwait(false);
            if (bytes.Content is null)
            {
                var hold = bytes.ProblemCode == ArtifactMaterializer.Codes.RegeneratedBytesDiffer;
                return await FinishAsync(claim, artifact, hold ? SyncState.RecoveryHold : SyncState.Failed, bytes.ProblemCode, bytes.ProblemDetail, null, null, null, cancellationToken).ConfigureAwait(false);
            }

            var remoteId = claim.RemoteFileId;
            var maybeSent = remoteId is not null;
            if (remoteId is null)
            {
                var journal = Journal.Read();
                if (!journal.Trustworthy)
                {
                    return await FinishAsync(claim, artifact, SyncState.RecoveryHold, Codes.IdentityJournalDamaged, journal.Problem, null, null, null, cancellationToken).ConfigureAwait(false);
                }

                if (!journal.Exists && anyRemoteIdentity)
                {
                    return await FinishAsync(claim, artifact, SyncState.RecoveryHold, Codes.IdentityJournalMissing,
                        "Remote identities were recorded but the upload journal is missing; new creates wait for reconciliation.", null, null, null, cancellationToken).ConfigureAwait(false);
                }

                // An identity journaled earlier (crash before it reached the database, or a restored older
                // database) is reused: the create may already have been sent with it.
                var known = journal.Find(claim.BindingId, artifact.LogicalKey, artifact.Sha256!);
                if (known is not null)
                {
                    remoteId = known.RemoteId;
                    maybeSent = true;
                }
                else
                {
                    remoteId = await store.GenerateIdAsync(cancellationToken).ConfigureAwait(false);
                    Journal.Append(journal, claim.BindingId, artifact.LogicalKey, artifact.Sha256!, remoteId, clock.UtcNowMilliseconds);
                }

                if (!await PersistRemoteIdAsync(claim.QueueId, remoteId, cancellationToken).ConfigureAwait(false))
                {
                    return Outcome.LostLease;
                }
            }

            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["logicalKey"] = artifact.LogicalKey,
                ["sha256"] = artifact.Sha256!,
                ["schemaVersion"] = artifact.PayloadSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["installationId"] = installationId.ToString("D"),
            };

            if (maybeSent && await store.GetAsync(remoteId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return await VerifiedAsync(claim, artifact, existing, bytes.Content.LongLength, remoteId, cancellationToken).ConfigureAwait(false);
            }

            var name = Path.GetFileName(artifact.RelativePath!);
            try
            {
                await store.CreateAsync(remoteId, claim.FolderKey, name, bytes.Content, properties, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteStoreException ex) when (ex.Kind is RemoteErrorKind.Transient or RemoteErrorKind.RateLimited or RemoteErrorKind.Conflict)
            {
                // The create may have reached the server. Read back the same ID before deciding anything.
                var after = await store.GetAsync(remoteId, cancellationToken).ConfigureAwait(false);
                if (after is not null)
                {
                    return await VerifiedAsync(claim, artifact, after, bytes.Content.LongLength, remoteId, cancellationToken).ConfigureAwait(false);
                }

                if (ex.Kind == RemoteErrorKind.Conflict)
                {
                    return await FinishAsync(claim, artifact, SyncState.RecoveryHold, Codes.RemoteConflict, ex.Message, remoteId, ex.HttpCode, null, cancellationToken).ConfigureAwait(false);
                }

                return await RetryAsync(claim, artifact, ex.Kind == RemoteErrorKind.RateLimited ? Codes.RateLimited : Codes.Transient, ex.Message, remoteId, ex.HttpCode, ex.RetryAfter, cancellationToken).ConfigureAwait(false);
            }

            var created = await store.GetAsync(remoteId, cancellationToken).ConfigureAwait(false);
            return created is null
                ? await RetryAsync(claim, artifact, Codes.NotVisibleYet, "The file was sent but cannot be read back yet; the same ID is checked next time.", remoteId, null, null, cancellationToken).ConfigureAwait(false)
                : await VerifiedAsync(claim, artifact, created, bytes.Content.LongLength, remoteId, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteStoreException ex)
        {
            var (state, code) = ex.Kind switch
            {
                RemoteErrorKind.Unauthorized => (SyncState.Failed, Codes.AuthorizationRequired),
                RemoteErrorKind.PermissionDenied => (SyncState.Failed, Codes.PermissionDenied),
                RemoteErrorKind.QuotaExceeded => (SyncState.Failed, Codes.StorageQuotaExceeded),
                RemoteErrorKind.Permanent => (SyncState.Failed, Codes.RemoteRejected),
                RemoteErrorKind.Conflict => (SyncState.RecoveryHold, Codes.RemoteConflict),
                RemoteErrorKind.RateLimited => (SyncState.Pending, Codes.RateLimited),
                _ => (SyncState.Pending, Codes.Transient),
            };
            var remoteId = await CurrentRemoteIdAsync(claim.QueueId, cancellationToken).ConfigureAwait(false);
            return state == SyncState.Pending
                ? await RetryAsync(claim, artifact, code, ex.Message, remoteId, ex.HttpCode, ex.RetryAfter, cancellationToken).ConfigureAwait(false)
                : await FinishAsync(claim, artifact, state, code, ex.Message, remoteId, ex.HttpCode, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException)
        {
            var remoteId = await CurrentRemoteIdAsync(claim.QueueId, cancellationToken).ConfigureAwait(false);
            return await RetryAsync(claim, artifact, Codes.Transient, "Network or storage error; will retry.", remoteId, null, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await renewal.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Renewal stops with the attempt.
            }
        }
    }

    /// <summary>Full jitter: random(0, min(3600 s, 5 s × 2^min(attempt, 10))), never shorter than a valid Retry-After.</summary>
    public static long BackoffMs(int attemptNo, TimeSpan? retryAfter, double random01)
    {
        var capSeconds = Math.Min(3600.0, 5.0 * Math.Pow(2, Math.Min(attemptNo, 10)));
        var delay = (long)(random01 * capSeconds * 1000);
        var floor = retryAfter is { } r && r > TimeSpan.Zero && r <= TimeSpan.FromHours(24) ? (long)r.TotalMilliseconds : 0;
        return Math.Max(delay, floor);
    }

    // ---------------------------------------------------------------- internals

    private Task<Outcome> VerifiedAsync(Claim claim, ArtifactRecord artifact, RemoteObject remote, long size, string remoteId, CancellationToken cancellationToken)
    {
        var identityMatches = remote.Properties.GetValueOrDefault("logicalKey") == artifact.LogicalKey
            && remote.Properties.GetValueOrDefault("sha256") == artifact.Sha256;
        var bytesMatch = remote.Size == size
            && (remote.ContentSha256 == artifact.Sha256 || (remote.ContentSha256 is null && artifact.Type != ArtifactType.Backup));
        return identityMatches && bytesMatch
            ? FinishAsync(claim, artifact, SyncState.Succeeded, null, null, remoteId, null, null, cancellationToken)
            : FinishAsync(claim, artifact, SyncState.RecoveryHold, Codes.RemoteMismatch,
                "The remote file with this ID does not match this artifact; it was not overwritten.", remoteId, null, null, cancellationToken);
    }

    private Task<Outcome> RetryAsync(Claim claim, ArtifactRecord artifact, string code, string? detail, string? remoteId, int? httpCode, TimeSpan? retryAfter, CancellationToken cancellationToken) =>
        FinishAsync(claim, artifact, SyncState.Pending, code, detail, remoteId, httpCode,
            clock.UtcNowMilliseconds + BackoffMs(claim.AttemptNo, retryAfter, _random()), cancellationToken);

    /// <summary>Ends the attempt, but only while this worker still owns the lease.</summary>
    private async Task<Outcome> FinishAsync(Claim claim, ArtifactRecord artifact, SyncState state, string? code, string? detail, string? remoteId,
        int? httpCode, long? nextAttemptUtc, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var item = await db.SyncQueue.SingleAsync(q => q.Id == claim.QueueId, cancellationToken).ConfigureAwait(false);
        if (item.State != SyncState.InProgress || item.LeaseOwner != LeaseOwner)
        {
            // The lease moved to another worker; that worker records the result for the same remote ID.
            return Outcome.LostLease;
        }

        item.State = state;
        item.LeaseOwner = null;
        item.LeaseExpiresUtc = null;
        item.LastErrorCode = code;
        item.RemoteFileId = remoteId ?? item.RemoteFileId;
        item.NextAttemptUtc = nextAttemptUtc ?? item.NextAttemptUtc;
        item.CompletedUtc = state == SyncState.Succeeded ? now : null;
        item.Version++;

        var attempt = await db.SyncHistory.SingleOrDefaultAsync(h => h.QueueId == claim.QueueId && h.AttemptNo == claim.AttemptNo && h.FinishedUtc == null, cancellationToken).ConfigureAwait(false);
        if (attempt is not null)
        {
            attempt.FinishedUtc = now;
            attempt.Outcome = state == SyncState.Succeeded ? SyncOutcome.Succeeded : SyncOutcome.Failed;
            attempt.HttpCode = httpCode;
            attempt.SafeError = code is null ? null : $"{code}: {detail}";
            attempt.RemoteFileId = item.RemoteFileId;
        }

        if (artifact.BackupId is { } backupId)
        {
            var backup = await db.Backups.SingleAsync(b => b.Id == backupId, cancellationToken).ConfigureAwait(false);
            backup.UploadState = state switch
            {
                SyncState.Succeeded => BackupUploadState.Uploaded,
                SyncState.Pending => BackupUploadState.Pending,
                _ => BackupUploadState.Failed,
            };
        }

        if (state is SyncState.Failed or SyncState.RecoveryHold)
        {
            AuditRecorder.Record(db, now, null, AuditRecorder.SystemActorName, state == SyncState.Failed ? "Sync.Failed" : "Sync.Held",
                "SyncQueue", item.Id.ToString(), null, new { logicalKey = artifact.LogicalKey, code });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return state switch
        {
            SyncState.Succeeded => Outcome.Succeeded,
            SyncState.Pending => Outcome.Retrying,
            SyncState.Failed => Outcome.Failed,
            _ => Outcome.RecoveryHold,
        };
    }

    private async Task<bool> PersistRemoteIdAsync(Guid queueId, string remoteId, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.SyncQueue.Where(q => q.Id == queueId && q.State == SyncState.InProgress && q.LeaseOwner == LeaseOwner && q.RemoteFileId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.RemoteFileId, remoteId).SetProperty(q => q.Version, q => q.Version + 1), cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    private async Task<string?> CurrentRemoteIdAsync(Guid queueId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SyncQueue.Where(q => q.Id == queueId).Select(q => q.RemoteFileId).SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RenewLeaseAsync(Guid queueId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(RenewIntervalMs, cancellationToken).ConfigureAwait(false);
            using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var expires = clock.UtcNowMilliseconds + LeaseMs;
            await db.SyncQueue.Where(q => q.Id == queueId && q.State == SyncState.InProgress && q.LeaseOwner == LeaseOwner)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.LeaseExpiresUtc, expires), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<BackupHealth> BackupHealthAsync(PosDbContext db, LocalDataPaths paths, CancellationToken cancellationToken)
    {
        var backups = await db.Backups.AsNoTracking().OrderByDescending(b => b.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        var present = backups.Where(b => Exists(paths, b.RelativePath)).ToList();
        var lastVerified = present.FirstOrDefault(b => b.VerificationState == BackupVerificationState.Verified);
        var free = FreeBytes(paths.Root);
        var threshold = Math.Max(2L * 1024 * 1024 * 1024, 2 * (backups.FirstOrDefault()?.SizeBytes ?? 0));
        return new BackupHealth(lastVerified?.VerifiedUtc, lastVerified is null ? null : Path.GetFileName(lastVerified.RelativePath),
            present.Count, free, threshold, free >= 0 && free < threshold);
    }

    internal static bool Exists(LocalDataPaths paths, string relativePath)
    {
        var full = Path.Combine(paths.Root, relativePath);
        return File.Exists(full) || Directory.Exists(full);
    }

    internal static long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public void Dispose() => _cycle.Dispose();
}
