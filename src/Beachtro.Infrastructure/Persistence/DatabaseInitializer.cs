using System.Data.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Runtime;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Persistence;

public sealed record PragmaReport(bool ForeignKeys, int BusyTimeoutMs, string JournalMode, int Synchronous)
{
    /// <summary>synchronous: 0 OFF, 1 NORMAL, 2 FULL, 3 EXTRA.</summary>
    public const int SynchronousFull = 2;

    public bool IsCompliant =>
        ForeignKeys
        && BusyTimeoutMs == SqlitePragmaInterceptor.BusyTimeoutMilliseconds
        && string.Equals(JournalMode, "wal", StringComparison.OrdinalIgnoreCase)
        && Synchronous == SynchronousFull;
}

public sealed class DatabaseNotReadyException(string message) : Exception(message);

/// <summary>
/// Test-profile database bring-up: migrate, set WAL, verify pragmas and integrity, and ensure the
/// installation identity row. Production migration (pre-migration backup, newer-schema refusal,
/// RecoveryRequired handling) is future work (IB-27) and is not enabled here.
/// </summary>
public sealed class DatabaseInitializer(IDbContextFactory<PosDbContext> contextFactory, IClock clock)
{
    public const int CurrentSchemaVersion = 1;

    public PragmaReport Initialize(RuntimeProfile profile)
    {
        if (profile != RuntimeProfile.Test)
        {
            throw new DatabaseNotReadyException("Automatic migration is enabled only for the Test profile in this build.");
        }

        using var db = contextFactory.CreateDbContext();
        db.Database.Migrate();

        db.Database.OpenConnection();
        try
        {
            var connection = db.Database.GetDbConnection();
            Scalar(connection, "PRAGMA journal_mode = WAL;");

            var integrity = Scalar(connection, "PRAGMA integrity_check;");
            if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
            {
                throw new DatabaseNotReadyException($"SQLite integrity_check failed: {integrity}");
            }

            using (var fkCheck = connection.CreateCommand())
            {
                fkCheck.CommandText = "PRAGMA foreign_key_check;";
                using var reader = fkCheck.ExecuteReader();
                if (reader.Read())
                {
                    throw new DatabaseNotReadyException($"SQLite foreign_key_check found a violation in table {reader.GetString(0)}.");
                }
            }

            var report = ReadPragmas(connection);
            if (!report.IsCompliant)
            {
                throw new DatabaseNotReadyException($"SQLite pragmas are not as required: {report}");
            }

            EnsureInstallationState(db);
            return report;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    public static PragmaReport ReadPragmas(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new PragmaReport(
            Scalar(connection, "PRAGMA foreign_keys;") == "1",
            int.Parse(Scalar(connection, "PRAGMA busy_timeout;"), System.Globalization.CultureInfo.InvariantCulture),
            Scalar(connection, "PRAGMA journal_mode;"),
            int.Parse(Scalar(connection, "PRAGMA synchronous;"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private void EnsureInstallationState(PosDbContext db)
    {
        if (db.InstallationState.Any())
        {
            return;
        }

        db.InstallationState.Add(new InstallationState
        {
            InstallationId = Guid.NewGuid(),
            SchemaVersion = CurrentSchemaVersion,
            Mode = InstallationMode.Normal,
            ProductionEnabled = false,
            LastObservedUtc = clock.UtcNowMilliseconds,
        });
        db.SaveChanges();
    }

    private static string Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
