using System.Globalization;
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
/// Minimal backup and controlled restore (sync/recovery design §Scheduling and backup package and
/// §Restore runbook; IB-12). A backup is a folder holding a consistent SQLite snapshot, a copy of
/// the number journal and a manifest with checksums. Zip packaging, retention and upload are IB-23.
/// </summary>
public sealed class BackupService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    NumberSeriesService numbering,
    IClock clock,
    WriteGate writeGate,
    LocalDataPaths paths) : IBackupService
{
    public const string ManifestFile = "manifest.json";
    public const string DatabaseFile = "pos.db";
    public const string JournalFile = "numbers.journal";
    public const string ManifestSchema = "backup-manifest.v1";

    public async Task<Result<BackupInfo>> CreateBackupAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupCreateVerify, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<BackupInfo>();
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
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

                w.WriteEndObject();
                w.WriteEndObject();
            });
            await File.WriteAllTextAsync(Path.Combine(tempFolder, ManifestFile), manifest, cancellationToken).ConfigureAwait(false);

            // 4. Only a complete, verified folder gets its final name.
            Directory.Move(tempFolder, finalFolder);
            var size = new FileInfo(Path.Combine(finalFolder, DatabaseFile)).Length;

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Backups.Add(new Backup
            {
                Id = backupId,
                RelativePath = Path.GetRelativePath(paths.Root, finalFolder).Replace('\\', '/'),
                SchemaVersion = Math.Max(1, facts.Migrations.Count),
                AppVersion = AppVersion,
                CreatedUtc = now,
                SizeBytes = size,
                Sha256 = dbHash,
                ManifestJson = manifest,
                LastSaleId = facts.LastSaleId,
                LastSaleNumber = facts.LastSaleNumber,
                VerifiedUtc = now,
                VerificationState = BackupVerificationState.Verified,
                UploadState = BackupUploadState.NotQueued,
            });
            AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Backup.Created", "Backup", backupId.ToString(), null,
                new { folder = Path.GetFileName(finalFolder), sizeBytes = size, sha256 = dbHash, lastSaleNumber = facts.LastSaleNumber });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result<BackupInfo>.Ok(new BackupInfo(backupId, finalFolder, size, dbHash, now, facts.LastSaleNumber));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            if (Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, recursive: true);
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
        return Result<BackupCandidateReport>.Ok(Validate(backupFolder, db.Database.GetMigrations().ToArray()));
    }

    public async Task<Result<RestoreResult>> RestoreAsync(UserSession session, string backupFolder, string reason, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupRestore, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<RestoreResult>();
        }

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

        var report = Validate(backupFolder, knownMigrations);
        if (!report.IsRestorable)
        {
            return Result<RestoreResult>.Fail(ResultStatus.Validation, "The backup cannot be restored: " + string.Join(" ", report.Problems));
        }

        if (report.InstallationId != currentInstallation)
        {
            // Cloning another installation's ledger would duplicate number series (sync design §Transaction contracts).
            return Result<RestoreResult>.Fail(ResultStatus.Validation, "This backup belongs to a different installation; a cross-installation restore needs a separately approved cutover.");
        }

        var candidateDb = Path.Combine(backupFolder, DatabaseFile);
        if (!UserExists(candidateDb, session.UserId))
        {
            return Result<RestoreResult>.Fail(ResultStatus.Validation, "Your account does not exist in this backup; restore with an account that does.");
        }

        var now = clock.UtcNowMilliseconds;
        var runId = Guid.NewGuid();
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var preserved = Path.Combine(paths.Recovery, $"restore_{stamp}_{runId:N}");

        // 1. Preserve the current database, WAL/SHM and journal as evidence (copies, never moves).
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

        // 3. Bring the restored schema forward, pause selling, and reconcile numbering from the live journal.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        Execute((SqliteConnection)db.Database.GetDbConnection(), "PRAGMA journal_mode = WAL;", openIfClosed: true);

        var state = await db.InstallationState.SingleAsync(cancellationToken).ConfigureAwait(false);
        state.Mode = InstallationMode.RecoveryRequired;
        state.Version++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var reconciliation = await numbering.ReconcileAsync(db, "restore", cancellationToken).ConfigureAwait(false);
        var prepared = await db.CheckoutRequests.CountAsync(r => r.State == RequestState.Prepared, cancellationToken).ConfigureAwait(false)
            + await db.CorrectionRequests.CountAsync(r => r.State == RequestState.Prepared, cancellationToken).ConfigureAwait(false);

        db.RecoveryRuns.Add(new RecoveryRun
        {
            Id = runId,
            StartedUtc = now,
            ActorUserId = session.UserId,
            SourceBackupId = await db.Backups.AnyAsync(b => b.Id == report.BackupId, cancellationToken).ConfigureAwait(false) ? report.BackupId : null,
            SourceChecksum = Sha256File(candidateDb),
            PreservedDbPath = Path.GetRelativePath(paths.Root, preserved).Replace('\\', '/'),
            NumberEvidenceJson = JsonSerializer.Serialize(new { blocked = reconciliation.Blocked.Select(b => b.SeriesKey), journalRepaired = reconciliation.JournalRepaired }),
            ReconciledLastSale = report.LastSaleNumber,
            Disposition = RecoveryDisposition.Hold,
            Notes = reason.Trim(),
            NewGenerationId = Guid.NewGuid(),
        });
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Recovery.Restored", "RecoveryRun", runId.ToString(), reason,
            new { backupId = report.BackupId, lastSaleNumber = report.LastSaleNumber, blockedSeries = reconciliation.Blocked.Count, preparedRequests = prepared });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<RestoreResult>.Ok(new RestoreResult(runId, preserved, reconciliation.Blocked, prepared));
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
