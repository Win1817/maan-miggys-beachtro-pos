using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Migrations.Sql;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Tests.Persistence;

/// <summary>T-04/T-19 provider checks against the real migrated SQLite schema.</summary>
public sealed class SchemaTests
{
    private static readonly string[] ExpectedTables =
    [
        "Users", "Roles", "Permissions", "UserRoles", "RolePermissions", "PolicyVersions", "TaxConfigurations",
        "BusinessProfiles", "BusinessSettings", "InstallationState", "CloudStorageSettings", "Categories", "Products",
        "Inventory", "InventoryMovements", "CashierShifts", "CashMovements", "Discounts", "Drafts", "CheckoutRequests",
        "NumberSeries", "Orders", "OrderItems", "ItemTaxComponents", "Payments", "CorrectionRequests", "Corrections",
        "CorrectionItems", "CorrectionTaxComponents", "RefundPayments", "PrintJobs", "ReportRuns", "ArtifactRecords",
        "SyncQueue", "SyncHistory", "Backups", "ScheduledJobs", "RecoveryRuns", "AuditLogs",
    ];

    internal static IReadOnlyList<string> ColumnsOf(PosDbContext db, string table) =>
        db.Model.GetEntityTypes().Single(e => e.GetTableName() == table).GetProperties().Select(p => p.GetColumnName()).ToArray();

    [Fact]
    public void Migration_creates_every_designed_table_and_matches_the_model()
    {
        using var database = new TestDatabase();
        using var db = database.NewContext();

        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.False(db.Database.HasPendingModelChanges(), "EF model has changes not captured in a migration.");

        var tables = database.Column("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND substr(name, 1, 4) <> '__EF'"); // EF's own history/lock tables
        Assert.Equal(ExpectedTables.Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Connection_pragmas_are_applied_and_read_back()
    {
        using var database = new TestDatabase();
        Assert.True(database.Pragmas.IsCompliant, database.Pragmas.ToString());

        // Read back on a fresh EF connection, not the one the initializer used.
        using var db = database.NewContext();
        db.Database.OpenConnection();
        var report = DatabaseInitializer.ReadPragmas(db.Database.GetDbConnection());
        Assert.Equal(new PragmaReport(ForeignKeys: true, BusyTimeoutMs: 5000, JournalMode: "wal", Synchronous: PragmaReport.SynchronousFull), report);
    }

    [Fact]
    public void Database_passes_integrity_and_foreign_key_checks()
    {
        using var database = new TestDatabase();
        Seed.Sale(database);

        Assert.Equal("ok", database.Scalar<string>("PRAGMA integrity_check"));
        Assert.Empty(database.Column("PRAGMA foreign_key_check"));
    }

    [Fact]
    public void No_column_uses_a_floating_point_or_numeric_affinity()
    {
        using var database = new TestDatabase();
        var offenders = new List<string>();
        foreach (var table in ExpectedTables)
        {
            foreach (var type in database.Column($"SELECT table_name.type FROM pragma_table_info('{table}') AS table_name"))
            {
                if (type is not ("INTEGER" or "TEXT"))
                {
                    offenders.Add($"{table}: {type}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_restrict_foreign_key_from_the_model_exists_in_the_database()
    {
        using var database = new TestDatabase();
        using var db = database.NewContext();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            var actual = database.Column($"SELECT \"from\" || '>' || \"table\" || ':' || on_delete FROM pragma_foreign_key_list('{table}')");
            foreach (var fk in entity.GetForeignKeys())
            {
                var expected = $"{fk.Properties.Single().GetColumnName()}>{fk.PrincipalEntityType.GetTableName()}:RESTRICT";
                Assert.Contains(expected, actual);
            }
        }
    }

    [Fact]
    public void Every_guard_trigger_is_installed()
    {
        using var database = new TestDatabase();
        using var db = database.NewContext();

        var installed = database.Column("SELECT name FROM sqlite_master WHERE type = 'trigger'");
        var expected = SchemaGuards.TriggerNames(t => ColumnsOf(db, t)).ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), installed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Installation_state_is_created_once_for_the_test_profile()
    {
        using var database = new TestDatabase();
        Assert.Equal(1L, database.Scalar<long>("SELECT count(*) FROM InstallationState"));
        Assert.Equal(0L, database.Scalar<long>("SELECT ProductionEnabled FROM InstallationState"));

        // Re-running initialization is idempotent.
        new DatabaseInitializer(database.Factory, new Runtime.FixedTestClock()).Initialize(Application.Runtime.RuntimeProfile.Test);
        Assert.Equal(1L, database.Scalar<long>("SELECT count(*) FROM InstallationState"));
    }

    [Fact]
    public void Production_profile_is_not_migrated_automatically()
    {
        using var database = new TestDatabase();
        var initializer = new DatabaseInitializer(database.Factory, new Runtime.FixedTestClock());
        Assert.Throws<DatabaseNotReadyException>(() => initializer.Initialize(Application.Runtime.RuntimeProfile.Production));
    }
}

/// <summary>
/// The baseline migration applies a frozen SQL file. This test proves the file equals what
/// <see cref="SchemaGuards"/> generates for the current model. To regenerate after an intended
/// guard change (only before the migration is released), run with BEACHTRO_REGENERATE_GUARDS=1.
/// </summary>
public sealed class GuardSqlTests
{
    [Fact]
    public void Frozen_guard_sql_matches_schema_guards()
    {
        using var database = new TestDatabase();
        using var db = database.NewContext();
        var expected = SchemaGuards.CreateStatements(t => SchemaTests.ColumnsOf(db, t)).ToArray();

        if (Environment.GetEnvironmentVariable("BEACHTRO_REGENERATE_GUARDS") == "1")
        {
            var path = Path.Combine(RepoRoot(), "src", "Beachtro.Infrastructure", "Persistence", "Migrations", "Sql", GuardSql.BaselineFile);
            var header = "-- Generated from SchemaGuards for the Baseline migration. Frozen: never edit after release.\n";
            File.WriteAllText(path, header + string.Join('\n', expected) + "\n");
            Assert.Fail("Guard SQL regenerated; rebuild and rerun without BEACHTRO_REGENERATE_GUARDS.");
        }

        Assert.Equal(expected, GuardSql.Load(GuardSql.BaselineFile));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Beachtro.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
