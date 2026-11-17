using System.Globalization;
using System.IO.Compression;
using Beachtro.Application.Ports;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Recovery;

public enum DatabaseCondition
{
    /// <summary>No database and no sign the PC was used: first run.</summary>
    New,

    /// <summary>The database matches this version.</summary>
    Current,

    /// <summary>This version has migrations the database does not have yet.</summary>
    NeedsUpdate,

    /// <summary>The database was written by a newer version; it must not be opened (POS-DEC-011).</summary>
    NewerThanApp,

    /// <summary>The database is gone but journals or backups show the PC was in use: restore, never start empty.</summary>
    MissingExpected,
}

public sealed record StartupInspection(DatabaseCondition Condition, IReadOnlyList<string> Applied, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown);

/// <summary>The verified package taken before migrating, and the schema it was taken at.</summary>
public sealed record PreUpdateBackup(Guid BackupId, string PackagePath, long CreatedUtc, IReadOnlyList<string> FromMigrations, BackupService.BuiltPackage Built);

/// <summary>
/// Manual updates (system design §Upgrade; IB-27, POS-DEC-011): inspect the database before EF touches
/// it, take a verified pre-update backup, migrate, record the update, and put the old database back if the
/// migration fails. An older version never opens data written by a newer one.
/// </summary>
public sealed class UpdateManager(IDbContextFactory<PosDbContext> contextFactory, BackupService backups, IClock clock, LocalDataPaths paths)
{
    public const string PreUpdatePrefix = "pos_preupdate";

    public StartupInspection Inspect()
    {
        string[] known;
        using (var db = contextFactory.CreateDbContext())
        {
            known = db.Database.GetMigrations().ToArray();
        }

        if (!File.Exists(paths.DatabaseFile) || new FileInfo(paths.DatabaseFile).Length == 0)
        {
            var used = File.Exists(paths.NumberJournalFile) || File.Exists(paths.UploadJournalFile)
                || (Directory.Exists(paths.Backups) && Directory.EnumerateFiles(paths.Backups, "*" + BackupService.PackageExtension).Any());
            return new StartupInspection(used ? DatabaseCondition.MissingExpected : DatabaseCondition.New, [], known, []);
        }

        var applied = BackupService.AppliedMigrations(paths.DatabaseFile);
        SqliteConnection.ClearAllPools();
        var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
        var pending = known.Except(applied, StringComparer.Ordinal).ToArray();
        var condition = unknown.Length > 0 ? DatabaseCondition.NewerThanApp
            : applied.Count == 0 ? DatabaseCondition.New
            : pending.Length > 0 ? DatabaseCondition.NeedsUpdate
            : DatabaseCondition.Current;
        return new StartupInspection(condition, applied, pending, unknown);
    }

    /// <summary>
    /// Writes and verifies the pre-update package. Throws <see cref="DatabaseNotReadyException"/> when it
    /// cannot: the update must not run without one.
    /// </summary>
    public PreUpdateBackup PrepareUpdate(StartupInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var id = Guid.NewGuid();
        var now = clock.UtcNowMilliseconds;
        (BackupService.BuiltPackage? Package, string? Problem) result;
        try
        {
            result = backups.BuildPackage(PreUpdatePrefix, id, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            throw new DatabaseNotReadyException("The update was not applied: the backup before updating could not be written (" + ex.Message + ").");
        }

        if (result.Package is null)
        {
            throw new DatabaseNotReadyException("The update was not applied: " + result.Problem);
        }

        return new PreUpdateBackup(id, result.Package.Package, now, inspection.Applied, result.Package);
    }

    /// <summary>After a successful migration: records the pre-update backup and the update itself (System actor).</summary>
    public void RecordUpdate(PreUpdateBackup backup, StartupInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(inspection);
        using var db = contextFactory.CreateDbContext();
        var now = clock.UtcNowMilliseconds;
        BackupService.RecordPackage(db, backup.BackupId, backup.Built, backup.CreatedUtc, null, AuditRecorder.SystemActorName, "Backup.PreUpdateCreated");
        AuditRecorder.Record(db, now, null, AuditRecorder.SystemActorName, "System.Updated", "InstallationState", null, null, new
        {
            appVersion = AppVersion,
            fromSchema = inspection.Applied.Count == 0 ? null : inspection.Applied[^1],
            toSchema = inspection.Pending.Count == 0 ? null : inspection.Pending[^1],
            migrationsApplied = inspection.Pending,
            preUpdateBackup = Path.GetFileName(backup.PackagePath),
        });
        db.SaveChanges();
    }

    /// <summary>
    /// A migration failed: keep the failed files as evidence and put the pre-update database back exactly.
    /// Returns the evidence folder.
    /// </summary>
    public string RevertFailedUpdate(PreUpdateBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        SqliteConnection.ClearAllPools();
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMilliseconds).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var evidence = Path.Combine(paths.Recovery, $"update_failed_{stamp}_{backup.BackupId:N}");
        Directory.CreateDirectory(evidence);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = paths.DatabaseFile + suffix;
            if (File.Exists(file))
            {
                File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)), overwrite: true);
            }
        }

        var staging = paths.DatabaseFile + ".reverting";
        using (var zip = ZipFile.OpenRead(backup.PackagePath))
        {
            var entry = zip.GetEntry(BackupService.DatabaseFile) ?? throw new InvalidDataException("The pre-update package has no database.");
            entry.ExtractToFile(staging, overwrite: true);
        }

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(paths.DatabaseFile + suffix))
            {
                File.Delete(paths.DatabaseFile + suffix);
            }
        }

        File.Move(staging, paths.DatabaseFile, overwrite: true);
        return evidence;
    }

    public static string AppVersion =>
        typeof(UpdateManager).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
