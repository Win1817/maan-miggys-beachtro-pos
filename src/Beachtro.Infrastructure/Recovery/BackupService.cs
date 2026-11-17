using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Recovery;
using Beachtro.Application.Runtime;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Numbering;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Runtime;
using Beachtro.Infrastructure.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Recovery;

/// <summary>
/// Backup and controlled restore (sync/recovery design §Scheduling and backup package and §Restore
/// runbook; IB-12, IB-23). A backup is one .zip package holding a consistent SQLite snapshot, copies of
/// the number and upload-identity journals and a manifest with per-member checksums. It is built in a
/// temporary folder, verified, packaged, checksummed and only then renamed to its final name.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    NumberSeriesService numbering,
    IClock clock,
    WriteGate writeGate,
    LocalDataPaths paths,
    Func<string, long>? freeSpace = null,
    Security.PosPasswordHasher? hasher = null) : IBackupService
{
    public const string ManifestFile = "manifest.json";
    public const string DatabaseFile = "pos.db";
    public const string JournalFile = "numbers.journal";
    public const string UploadJournalFile = "uploads.journal";
    public const string ManifestSchema = "backup-manifest.v1";
    public const string PackageExtension = ".zip";

    /// <summary>Generations kept locally (sync design §Retention recommendation).</summary>
    public const int KeepGenerations = 7;

    private static readonly HashSet<string> AllowedMembers = new(StringComparer.Ordinal) { ManifestFile, DatabaseFile, JournalFile, UploadJournalFile };
    private readonly Func<string, long> _freeSpace = freeSpace ?? Sync.SyncService.FreeBytes;
    private readonly Security.PosPasswordHasher _hasher = hasher ?? new Security.PosPasswordHasher();

    public async Task<Result<BackupInfo>> CreateBackupAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupCreateVerify, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<BackupInfo>();
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);

        // Snapshot + package + margin must fit; never start a backup that would fill the disk.
        var databaseBytes = File.Exists(paths.DatabaseFile) ? new FileInfo(paths.DatabaseFile).Length : 0;
        var needed = (3 * databaseBytes) + (64L * 1024 * 1024);
        var free = _freeSpace(paths.Backups);
        if (free >= 0 && free < needed)
        {
            return Result<BackupInfo>.Fail(ResultStatus.StorageUnavailable,
                $"Not enough free disk space for a backup (about {needed / (1024 * 1024)} MB needed, {free / (1024 * 1024)} MB free). Free some space; existing backups were kept.");
        }

        var now = clock.UtcNowMilliseconds;
        var backupId = Guid.NewGuid();
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var finalFolder = Path.Combine(paths.Backups, $"pos_backup_{stamp}_{backupId:N}");
        var tempFolder = finalFolder + ".partial";
        Directory.CreateDirectory(tempFolder);

        try
        {
            // 1. Consistent snapshot through the SQLite Online Backup API (never a raw file copy).
            var snapshotPath = Path.Combine(tempFolder, DatabaseFile);
            using (var source = OpenConnection(paths.DatabaseFile, readOnly: false))
            using (var target = OpenConnection(snapshotPath, readOnly: false, pooling: false))
            {
                source.BackupDatabase(target);
                Execute(target, "PRAGMA journal_mode = DELETE;");
            }

            // 2. Verify the copy, not the live database.
            var problems = VerifyDatabase(snapshotPath);
            if (problems.Count > 0)
            {
                Directory.Delete(tempFolder, recursive: true);
                return Result<BackupInfo>.Fail(ResultStatus.StorageUnavailable, "The backup copy failed verification: " + string.Join(" ", problems));
            }

            // 3. Journal checkpoint as evidence (the live journal is never restored over).
            var journal = numbering.Journal.Read();
            if (File.Exists(paths.NumberJournalFile))
            {
                File.Copy(paths.NumberJournalFile, Path.Combine(tempFolder, JournalFile));
            }

            var uploadJournalPath = Path.Combine(tempFolder, UploadJournalFile);
            if (File.Exists(paths.UploadJournalFile))
            {
                File.Copy(paths.UploadJournalFile, uploadJournalPath);
            }

            var facts = ReadFacts(snapshotPath);
            var dbHash = Sha256File(snapshotPath);
            var journalPath = Path.Combine(tempFolder, JournalFile);
            var manifest = CanonicalJson.Write(w =>
            {
                w.WriteStartObject();
                w.WriteString("schema", ManifestSchema);
                w.WriteString("backupId", backupId.ToString("D"));
                w.WriteNumber("createdUtc", now);
                w.WriteString("installationId", facts.InstallationId.ToString("D"));
                w.WriteString("appVersion", AppVersion);
                w.WriteStartArray("migrations");
                foreach (var m in facts.Migrations)
                {
                    w.WriteStringValue(m);
                }

                w.WriteEndArray();
                if (facts.LastSaleNumber is null)
                {
                    w.WriteNull("lastSaleNumber");
                }
                else
                {
                    w.WriteString("lastSaleNumber", facts.LastSaleNumber);
                }

                w.WriteNumber("orderCount", facts.OrderCount);
                w.WriteNumber("orderTotalM", facts.OrderTotalM);
                w.WriteNumber("correctionCount", facts.CorrectionCount);
                w.WriteNumber("correctionTotalM", facts.CorrectionTotalM);
                w.WriteStartObject("journal");
                w.WriteBoolean("present", File.Exists(journalPath));
                w.WriteNumber("records", journal.Records.Count);
                w.WriteString("lastHash", journal.LastHash);
                w.WriteEndObject();
                w.WriteStartObject("files");
                w.WriteString(DatabaseFile, dbHash);
                if (File.Exists(journalPath))
                {
                    w.WriteString(JournalFile, Sha256File(journalPath));
                }

                if (File.Exists(uploadJournalPath))
                {
                    w.WriteString(UploadJournalFile, Sha256File(uploadJournalPath));
                }

                w.WriteEndObject();
                w.WriteEndObject();
            });
            await File.WriteAllTextAsync(Path.Combine(tempFolder, ManifestFile), manifest, cancellationToken).ConfigureAwait(false);

            // 4. Package, flush, re-verify the package itself, then give it its final name.
            var package = finalFolder + PackageExtension;
            var partial = package + ".partial";
            ZipFile.CreateFromDirectory(tempFolder, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
            using (var flush = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                flush.Flush(flushToDisk: true);
            }

            var packageProblems = VerifyPackage(partial);
            if (packageProblems.Count > 0)
            {
                File.Delete(partial);
                Directory.Delete(tempFolder, recursive: true);
                return Result<BackupInfo>.Fail(ResultStatus.StorageUnavailable, "The backup package failed verification: " + string.Join(" ", packageProblems));
            }

            var packageHash = Sha256File(partial);
            File.Move(partial, package);
            Directory.Delete(tempFolder, recursive: true);
            var size = new FileInfo(package).Length;
            var relative = Path.GetRelativePath(paths.Root, package).Replace('\\', '/');

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Backups.Add(new Backup
            {
                Id = backupId,
                RelativePath = relative,
                SchemaVersion = Math.Max(1, facts.Migrations.Count),
                AppVersion = AppVersion,
                CreatedUtc = now,
                SizeBytes = size,
                Sha256 = packageHash,
                ManifestJson = manifest,
                LastSaleId = facts.LastSaleId,
                LastSaleNumber = facts.LastSaleNumber,
                VerifiedUtc = now,
                VerificationState = BackupVerificationState.Verified,
                UploadState = BackupUploadState.NotQueued,
            });

            // The package is complete, so its artifact is created Ready; it can never be rebuilt from a newer database.
            db.ArtifactRecords.Add(new ArtifactRecord
            {
                Id = Guid.NewGuid(),
                LogicalKey = $"backup/{backupId:D}/schema1",
                Type = ArtifactType.Backup,
                SourceId = backupId,
                BackupId = backupId,
                PayloadSchemaVersion = 1,
                RelativePath = relative,
                Sha256 = packageHash,
                SizeBytes = size,
                State = ArtifactState.Ready,
                CreatedUtc = now,
                MaterializedUtc = now,
            });
            AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Backup.Created", "Backup", backupId.ToString(), null,
                new { package = Path.GetFileName(package), sizeBytes = size, sha256 = packageHash, databaseSha256 = dbHash, lastSaleNumber = facts.LastSaleNumber });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var pruned = await ApplyRetentionAsync(db, now, session, cancellationToken).ConfigureAwait(false);
            return Result<BackupInfo>.Ok(new BackupInfo(backupId, package, size, packageHash, now, facts.LastSaleNumber, pruned));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            if (Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, recursive: true);
            }

            if (File.Exists(finalFolder + PackageExtension + ".partial"))
            {
                File.Delete(finalFolder + PackageExtension + ".partial");
            }

            return Result<BackupInfo>.Fail(ResultStatus.StorageUnavailable, "The backup could not be written. The live database was not changed.");
        }
    }

    public async Task<Result<BackupCandidateReport>> ValidateCandidateAsync(UserSession session, string backupFolder, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupRestore, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<BackupCandidateReport>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var (folder, unpackProblem) = Unpack(backupFolder);
        return Result<BackupCandidateReport>.Ok(unpackProblem is not null
            ? new BackupCandidateReport(false, [unpackProblem], null, null, null, 0)
            : Validate(folder!, db.Database.GetMigrations().ToArray()));
    }

    public async Task<Result<RestoreResult>> RestoreAsync(UserSession session, string backupFolder, string reason, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupRestore, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<RestoreResult>();
        }

        return await RestoreCoreAsync(backupFolder, reason, newDevice: null, session.UserId, session.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<RestoreResult>> RestoreOnNewDeviceAsync(string packagePath, string username, string password, string reason, CancellationToken cancellationToken = default)
    {
        await using (var current = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await current.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
            if (state.BootstrapCompletedUtc is not null || await current.Users.AnyAsync(cancellationToken).ConfigureAwait(false)
                || await current.Orders.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result<RestoreResult>.Fail(ResultStatus.Forbidden,
                    "Restoring onto a new PC is only possible before the first owner account is created. Sign in and use the Recovery page instead.");
            }
        }

        return await RestoreCoreAsync(packagePath, reason, new NewDeviceCredentials(username ?? string.Empty, password ?? string.Empty), Guid.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record NewDeviceCredentials(string Username, string Password);

    /// <summary>
    /// Shared restore (runbook steps 2–6). <paramref name="newDevice"/> null: same installation, signed-in
    /// actor. Otherwise a replacement PC: the actor is the backup account whose credentials are given, the
    /// backup's installation identity is adopted, and journals that cannot prove what the lost PC did
    /// lead to holds rather than guesses (POS-DEC-010).
    /// </summary>
    private async Task<Result<RestoreResult>> RestoreCoreAsync(
        string backupFolder, string reason, NewDeviceCredentials? newDevice, Guid actorId, string actorName, CancellationToken cancellationToken)
    {
        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result<RestoreResult>.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        string[] knownMigrations;
        Guid currentInstallation;
        await using (var current = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            knownMigrations = current.Database.GetMigrations().ToArray();
            currentInstallation = await current.InstallationState.Select(s => s.InstallationId).SingleAsync(cancellationToken).ConfigureAwait(false);
        }

        var (unpacked, unpackProblem) = Unpack(backupFolder);
        if (unpackProblem is not null)
        {
            return Result<RestoreResult>.Fail(ResultStatus.Validation, "The backup cannot be restored: " + unpackProblem);
        }

        backupFolder = unpacked!;
        var report = Validate(backupFolder, knownMigrations);
        if (!report.IsRestorable)
        {
            return Result<RestoreResult>.Fail(ResultStatus.Validation, "The backup cannot be restored: " + string.Join(" ", report.Problems));
        }

        var candidateDb = Path.Combine(backupFolder, DatabaseFile);
        if (newDevice is null)
        {
            if (report.InstallationId != currentInstallation)
            {
                // Cloning another installation's ledger would duplicate number series (sync design §Transaction contracts).
                return Result<RestoreResult>.Fail(ResultStatus.Validation, "This backup belongs to a different installation; a cross-installation restore needs a separately approved cutover.");
            }

            if (!UserExists(candidateDb, actorId))
            {
                return Result<RestoreResult>.Fail(ResultStatus.Validation, "Your account does not exist in this backup; restore with an account that does.");
            }
        }
        else
        {
            var account = CheckBackupAccount(candidateDb, newDevice);
            if (account is null)
            {
                // Same message for unknown name, wrong password, inactive or not allowed: no account probing.
                return Result<RestoreResult>.Fail(ResultStatus.Unauthenticated,
                    "Those sign-in details do not match an Owner or Administrator account in this backup.");
            }

            (actorId, actorName) = account.Value;
        }

        var now = clock.UtcNowMilliseconds;
        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var preserved = Path.Combine(paths.Recovery, $"restore_{stamp}_{runId:N}");

        // What the live journals can still prove, before anything changes.
        var liveNumbers = numbering.Journal.Read();
        var numbersUnprovable = !liveNumbers.Exists || !liveNumbers.Trustworthy
            || liveNumbers.Records.Any(r => r.InstallationId != report.InstallationId);
        var uploadJournalMissing = !File.Exists(paths.UploadJournalFile);

        // 1. Preserve the current database, WAL/SHM and journals as evidence (copies, never moves).
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(preserved);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = paths.DatabaseFile + suffix;
            if (File.Exists(file))
            {
                File.Copy(file, Path.Combine(preserved, Path.GetFileName(file)));
            }
        }

        if (File.Exists(paths.NumberJournalFile))
        {
            File.Copy(paths.NumberJournalFile, Path.Combine(preserved, JournalFile));
        }

        if (!uploadJournalMissing)
        {
            File.Copy(paths.UploadJournalFile, Path.Combine(preserved, UploadJournalFile));
        }

        // 2. Replace the live database with the validated candidate. Stale WAL/SHM must not be replayed onto it.
        var staging = paths.DatabaseFile + ".restoring";
        File.Copy(candidateDb, staging, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(paths.DatabaseFile + suffix))
            {
                File.Delete(paths.DatabaseFile + suffix);
            }
        }

        File.Move(staging, paths.DatabaseFile, overwrite: true);

        // A new PC has no upload journal: the backup's copy keeps the identities known at backup time,
        // but the lost PC may have uploaded more afterwards, so unknown identities are held.
        var packagedUploads = Path.Combine(backupFolder, UploadJournalFile);
        var uploadsMayBeStale = false;
        if (uploadJournalMissing && File.Exists(packagedUploads))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.UploadJournalFile)!);
            File.Copy(packagedUploads, paths.UploadJournalFile);
            uploadsMayBeStale = true;
        }

        // 3. Bring the restored schema forward, pause selling, and reconcile numbering from the live journal.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        Execute((SqliteConnection)db.Database.GetDbConnection(), "PRAGMA journal_mode = WAL;", openIfClosed: true);

        var state = await db.InstallationState.SingleAsync(cancellationToken).ConfigureAwait(false);
        state.Mode = InstallationMode.RecoveryRequired;
        state.Version++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await numbering.ReconcileAsync(db, "restore", cancellationToken).ConfigureAwait(false);
        var possiblyUsed = numbersUnprovable ? await BlockPossiblyUsedSeriesAsync(db, report.CreatedUtc, now, cancellationToken).ConfigureAwait(false) : 0;
        var uploads = await Sync.SyncReconciler.AfterRestoreAsync(db, new Sync.UploadJournal(paths.UploadJournalFile), now, cancellationToken, uploadsMayBeStale)
            .ConfigureAwait(false);
        var prepared = await db.CheckoutRequests.CountAsync(r => r.State == RequestState.Prepared, cancellationToken).ConfigureAwait(false)
            + await db.CorrectionRequests.CountAsync(r => r.State == RequestState.Prepared, cancellationToken).ConfigureAwait(false);
        var blocked = await db.NumberSeries.AsNoTracking().Where(s => s.Status == NumberSeriesStatus.Blocked).OrderBy(s => s.SeriesKey)
            .Select(s => new SeriesHold(s.SeriesKey, s.LastReservedSequence, s.EvidenceRef ?? "Blocked"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        db.RecoveryRuns.Add(new RecoveryRun
        {
            Id = runId,
            StartedUtc = now,
            ActorUserId = actorId,
            SourceBackupId = await db.Backups.AnyAsync(b => b.Id == report.BackupId, cancellationToken).ConfigureAwait(false) ? report.BackupId : null,
            SourceChecksum = Sha256File(candidateDb),
            PreservedDbPath = Path.GetRelativePath(paths.Root, preserved).Replace('\\', '/'),
            NumberEvidenceJson = JsonSerializer.Serialize(new { blocked = blocked.Select(b => b.SeriesKey), numbersUnprovable, possiblyUsed }),
            ReconciledLastSale = report.LastSaleNumber,
            Disposition = RecoveryDisposition.Hold,
            Notes = (newDevice is null ? string.Empty : "Replacement PC. ") + reason.Trim(),
            NewGenerationId = Guid.NewGuid(),
            UploadIdentityCutoffUtc = newDevice is null ? null : now,
        });
        AuditRecorder.Record(db, now, actorId, actorName, newDevice is null ? "Recovery.Restored" : "Recovery.RestoredOnNewDevice", "RecoveryRun", runId.ToString(), reason,
            new { backupId = report.BackupId, lastSaleNumber = report.LastSaleNumber, blockedSeries = blocked.Count, possiblyUsed, preparedRequests = prepared,
                uploadsReset = uploads.Reset, uploadsAdopted = uploads.Adopted, uploadsHeld = uploads.Held, replacedInstallation = newDevice is null ? (Guid?)null : currentInstallation });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<RestoreResult>.Ok(new RestoreResult(runId, preserved, blocked, prepared));
    }

    /// <summary>
    /// The live journal cannot say what the lost PC numbered after the backup, so a series for every
    /// business date from the backup's date to today is blocked until the owner proves its high-water mark.
    /// </summary>
    private static async Task<int> BlockPossiblyUsedSeriesAsync(PosDbContext db, long backupUtc, long now, CancellationToken cancellationToken)
    {
        var calendarJson = await db.PolicyVersions.AsNoTracking()
            .Where(p => p.Kind == PolicyKind.Calendar && p.ApprovalState != Domain.Financial.Calculation.ProfileApprovalState.Draft && p.EffectiveFromUtc <= now)
            .OrderByDescending(p => p.Revision).Select(p => p.CanonicalPayloadJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var calendar = calendarJson is null ? Domain.Calendar.CalendarPolicy.ManilaMidnight
            : new Domain.Calendar.CalendarPolicy(ProfilePayloads.ReadCalendar(calendarJson).TimeZoneId, ProfilePayloads.ReadCalendar(calendarJson).CutoffMinute);
        var first = DateOnly.ParseExact(calendar.BusinessDateFor(Math.Min(backupUtc, now)), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = DateOnly.ParseExact(calendar.BusinessDateFor(now), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (last.DayNumber - first.DayNumber > MaxRecoveryDays)
        {
            first = last.AddDays(-MaxRecoveryDays);
        }

        var added = 0;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var key = NumberSeriesService.SeriesKeyFor(date);
            var series = await db.NumberSeries.SingleOrDefaultAsync(s => s.SeriesKey == key, cancellationToken).ConfigureAwait(false);
            if (series is null)
            {
                db.NumberSeries.Add(new NumberSeries
                {
                    SeriesKey = key,
                    BusinessDate = date,
                    Prefix = NumberSeriesService.Prefix,
                    LastReservedSequence = 0,
                    Status = NumberSeriesStatus.Blocked,
                    LastReconciledUtc = now,
                    EvidenceRef = PossiblyUsedEvidence,
                });
                added++;
            }
            else if (series.Status == NumberSeriesStatus.Ready)
            {
                series.Status = NumberSeriesStatus.Blocked;
                series.EvidenceRef = PossiblyUsedEvidence;
                series.Version++;
                added++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <summary>Upper bound on how many days of possibly-used series one restore blocks.</summary>
    public const int MaxRecoveryDays = 366;

    public const string PossiblyUsedEvidence = "The lost PC may have used numbers after the backup; enter the highest number known to be used (0 if none).";

    /// <summary>The backup account for a new-PC restore: active, correct password, and allowed to restore.</summary>
    private (Guid UserId, string DisplayName)? CheckBackupAccount(string candidateDb, NewDeviceCredentials credentials)
    {
        using var c = OpenConnection(candidateDb, readOnly: true, pooling: false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, DisplayName, PasswordHash FROM Users WHERE NormalizedUsername = $n AND IsActive = 1;";
        cmd.Parameters.AddWithValue("$n", UsernameRules.Normalize(credentials.Username));
        string? id = null, name = null, hash = null;
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
            {
                (id, name, hash) = (r.GetString(0), r.GetString(1), r.GetString(2));
            }
        }

        if (hash is null)
        {
            _hasher.Verify(_hasher.DummyHash, credentials.Password);
            return null;
        }

        if (_hasher.Verify(hash, credentials.Password) == Security.PasswordCheck.Failed)
        {
            return null;
        }

        using var roles = c.CreateCommand();
        roles.CommandText = "SELECT r.Key FROM UserRoles ur JOIN Roles r ON r.Id = ur.RoleId WHERE ur.UserId = $id;";
        roles.Parameters.AddWithValue("$id", id);
        using var rr = roles.ExecuteReader();
        var allowed = false;
        while (rr.Read())
        {
            allowed |= RoleTemplates.Grants.TryGetValue(rr.GetString(0), out var grants) && grants.Contains(Capabilities.BackupRestore);
        }

        return allowed ? (Guid.Parse(id!), name!) : null;
    }

    public async Task<Result<Unit>> ReopenAsync(UserSession session, string notes, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupRestore, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(notes, "Notes", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var blocked = await db.NumberSeries.Where(s => s.Status == NumberSeriesStatus.Blocked).Select(s => s.SeriesKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (blocked.Count > 0)
        {
            return Result.Fail(ResultStatus.Conflict, $"Resolve the number series on hold first: {string.Join(", ", blocked)}.");
        }

        var prepared = await db.CheckoutRequests.CountAsync(r => r.State == RequestState.Prepared, cancellationToken).ConfigureAwait(false)
            + await db.CorrectionRequests.CountAsync(r => r.State == RequestState.Prepared, cancellationToken).ConfigureAwait(false);
        if (prepared > 0)
        {
            return Result.Fail(ResultStatus.Conflict, $"Close the {prepared} unfinished payment(s) first; they may have been completed on the lost PC.");
        }

        var state = await db.InstallationState.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (state.Mode == InstallationMode.Normal)
        {
            return Result.Ok();
        }

        var now = clock.UtcNowMilliseconds;
        state.Mode = InstallationMode.Normal;
        state.Version++;
        foreach (var run in await db.RecoveryRuns.Where(r => r.Disposition == RecoveryDisposition.Hold).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            run.Disposition = RecoveryDisposition.Completed;
            run.FinishedUtc = now;
            run.Notes = $"{run.Notes} | Reopened: {notes.Trim()}";
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Recovery.Reopened", "InstallationState", state.InstallationId.ToString(), notes, null);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    // ---------------------------------------------------------------- retention and packages

    /// <summary>
    /// Keeps at least <see cref="KeepGenerations"/> local generations and never deletes the two newest
    /// verified backups, any backup still waiting to upload, or one under an explicit protection date.
    /// Pruning is by count only, never because an upload succeeded. The Backups row stays as history.
    /// </summary>
    internal async Task<int> ApplyRetentionAsync(PosDbContext db, long now, UserSession session, CancellationToken cancellationToken)
    {
        var present = (await db.Backups.OrderByDescending(b => b.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(b => Sync.SyncService.Exists(paths, b.RelativePath)).ToList();
        if (present.Count <= KeepGenerations)
        {
            return 0;
        }

        var protectedIds = present.Where(b => b.VerificationState == BackupVerificationState.Verified).Take(2).Select(b => b.Id)
            .Concat(present.Where(b => b.UploadState is BackupUploadState.Pending or BackupUploadState.Failed).Select(b => b.Id))
            .Concat(present.Where(b => b.RetentionProtectUntilUtc > now).Select(b => b.Id))
            .ToHashSet();
        var pruned = 0;
        foreach (var backup in present.Skip(KeepGenerations).Where(b => !protectedIds.Contains(b.Id)))
        {
            var full = Path.Combine(paths.Root, backup.RelativePath);
            try
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
                else if (Directory.Exists(full))
                {
                    Directory.Delete(full, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Backup.Pruned", "Backup", backup.Id.ToString(), null,
                new { file = Path.GetFileName(backup.RelativePath), keep = KeepGenerations });
            pruned++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pruned;
    }

    /// <summary>A package is extracted into a fresh recovery work folder (only known member names); a folder is used as is.</summary>
    private (string? Folder, string? Problem) Unpack(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "Choose a backup.");
        }

        if (Directory.Exists(path))
        {
            return (path, null);
        }

        if (!File.Exists(path) || !path.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The backup package was not found.");
        }

        var target = Path.Combine(paths.Recovery, $"candidate_{Guid.NewGuid():N}");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Any(e => !AllowedMembers.Contains(e.FullName)))
            {
                return (null, "The package contains unexpected files.");
            }

            Directory.CreateDirectory(target);
            foreach (var entry in zip.Entries)
            {
                entry.ExtractToFile(Path.Combine(target, entry.FullName));
            }

            return (target, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (null, "The backup package is damaged or unreadable.");
        }
    }

    /// <summary>Checks every member against the manifest inside the package.</summary>
    private static List<string> VerifyPackage(string package)
    {
        var problems = new List<string>();
        using var zip = ZipFile.OpenRead(package);
        var manifestEntry = zip.GetEntry(ManifestFile);
        if (manifestEntry is null)
        {
            return ["The package has no manifest."];
        }

        using var manifestStream = manifestEntry.Open();
        using var doc = JsonDocument.Parse(manifestStream);
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            var entry = zip.GetEntry(file.Name);
            if (entry is null)
            {
                problems.Add($"Member {file.Name} is missing.");
                continue;
            }

            using var stream = entry.Open();
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Value.GetString())
            {
                problems.Add($"Member {file.Name} does not match its checksum.");
            }
        }

        return problems;
    }

    // ---------------------------------------------------------------- internals

    private static string AppVersion => typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0";

    private sealed record SnapshotFacts(Guid InstallationId, IReadOnlyList<string> Migrations, Guid? LastSaleId, string? LastSaleNumber,
        long OrderCount, long OrderTotalM, long CorrectionCount, long CorrectionTotalM);

    private static BackupCandidateReport Validate(string folder, IReadOnlyCollection<string> knownMigrations)
    {
        var problems = new List<string>();
        var manifestPath = Path.Combine(folder ?? string.Empty, ManifestFile);
        var dbPath = Path.Combine(folder ?? string.Empty, DatabaseFile);
        if (!File.Exists(manifestPath) || !File.Exists(dbPath))
        {
            return new BackupCandidateReport(false, ["The folder is not a complete backup (manifest or database missing)."], null, null, null, 0);
        }

        Guid? backupId = null;
        long created = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetString() != ManifestSchema)
            {
                problems.Add("Unsupported manifest version.");
            }

            backupId = Guid.Parse(root.GetProperty("backupId").GetString()!);
            created = root.GetProperty("createdUtc").GetInt64();
            foreach (var file in root.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(folder!, file.Name);
                if (file.Name.Contains('/', StringComparison.Ordinal) || file.Name.Contains('\\', StringComparison.Ordinal) || !File.Exists(path))
                {
                    problems.Add($"Backup member {file.Name} is missing or invalid.");
                }
                else if (Sha256File(path) != file.Value.GetString())
                {
                    problems.Add($"Backup member {file.Name} does not match its checksum.");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            problems.Add("The manifest is unreadable.");
        }

        if (problems.Count > 0)
        {
            return new BackupCandidateReport(false, problems, backupId, null, null, created);
        }

        problems.AddRange(VerifyDatabase(dbPath));
        SnapshotFacts? facts = null;
        if (problems.Count == 0)
        {
            facts = ReadFacts(dbPath);
            var unknown = facts.Migrations.Except(knownMigrations, StringComparer.Ordinal).ToArray();
            if (unknown.Length > 0)
            {
                problems.Add("The backup comes from a newer application version; it cannot be opened safely by this version.");
            }
        }

        return new BackupCandidateReport(problems.Count == 0, problems, backupId, facts?.InstallationId, facts?.LastSaleNumber, created);
    }

    private static List<string> VerifyDatabase(string path)
    {
        var problems = new List<string>();
        try
        {
            using var c = OpenConnection(path, readOnly: true, pooling: false);
            var integrity = Scalar(c, "PRAGMA integrity_check;");
            if (integrity != "ok")
            {
                problems.Add($"Integrity check failed: {integrity}.");
            }

            using var fk = c.CreateCommand();
            fk.CommandText = "PRAGMA foreign_key_check;";
            using var reader = fk.ExecuteReader();
            if (reader.Read())
            {
                problems.Add($"Foreign key check failed in table {reader.GetString(0)}.");
            }
        }
        catch (SqliteException ex)
        {
            problems.Add($"The database file cannot be opened ({ex.SqliteErrorCode}).");
        }

        return problems;
    }

    private static SnapshotFacts ReadFacts(string path)
    {
        using var c = OpenConnection(path, readOnly: true, pooling: false);
        var installation = Guid.Parse(Scalar(c, "SELECT InstallationId FROM InstallationState WHERE Id = 1;"));
        var migrations = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                migrations.Add(r.GetString(0));
            }
        }

        Guid? lastId = null;
        string? lastNumber = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, TransactionNumber FROM Orders ORDER BY CompletedUtc DESC, Sequence DESC LIMIT 1;";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                lastId = Guid.Parse(r.GetString(0));
                lastNumber = r.GetString(1);
            }
        }

        return new SnapshotFacts(installation, migrations, lastId, lastNumber,
            long.Parse(Scalar(c, "SELECT count(*) FROM Orders;"), CultureInfo.InvariantCulture),
            long.Parse(Scalar(c, "SELECT coalesce(sum(TotalM), 0) FROM Orders;"), CultureInfo.InvariantCulture),
            long.Parse(Scalar(c, "SELECT count(*) FROM Corrections;"), CultureInfo.InvariantCulture),
            long.Parse(Scalar(c, "SELECT coalesce(sum(TotalM), 0) FROM Corrections;"), CultureInfo.InvariantCulture));
    }

    private static bool UserExists(string path, Guid userId)
    {
        using var c = OpenConnection(path, readOnly: true, pooling: false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM Users WHERE Id = $id AND IsActive = 1;";
        cmd.Parameters.AddWithValue("$id", userId.ToString("D"));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static SqliteConnection OpenConnection(string path, bool readOnly, bool pooling = true)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = pooling,
            ForeignKeys = true,
        }.ToString());
        c.Open();
        return c;
    }

    private static string Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(SqliteConnection c, string sql, bool openIfClosed = false)
    {
        if (openIfClosed && c.State != System.Data.ConnectionState.Open)
        {
            c.Open();
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
