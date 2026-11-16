using Beachtro.Application.Ports;
using Beachtro.Application.Runtime;
using Beachtro.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Tests.Persistence;

/// <summary>A migrated, file-backed SQLite database in a temp folder (real provider, real DDL).</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _dir = new();

    public TestDatabase()
    {
        DatabaseFile = Path.Combine(_dir.Path, "pos.db");
        var builder = new DbContextOptionsBuilder<PosDbContext>();
        PersistenceOptions.Configure(builder, DatabaseFile);
        Options = builder.Options;
        Factory = new ContextFactory(Options);
        Pragmas = new DatabaseInitializer(Factory, new FixedClock()).Initialize(RuntimeProfile.Test);
    }

    public string DatabaseFile { get; }
    public DbContextOptions<PosDbContext> Options { get; }
    public IDbContextFactory<PosDbContext> Factory { get; }
    public PragmaReport Pragmas { get; }

    public PosDbContext NewContext() => new(Options);

    /// <summary>Executes raw SQL on a fresh connection with the production pragmas applied.</summary>
    public int Execute(string sql)
    {
        using var connection = new SqliteConnection(PersistenceOptions.ConnectionString(DatabaseFile));
        connection.Open();
        SqlitePragmaInterceptor.Apply(connection);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection(PersistenceOptions.ConnectionString(DatabaseFile));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public List<string> Column(string sql)
    {
        using var connection = new SqliteConnection(PersistenceOptions.ConnectionString(DatabaseFile));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }

    public void Dispose() => _dir.Dispose();

    private sealed class ContextFactory(DbContextOptions<PosDbContext> options) : IDbContextFactory<PosDbContext>
    {
        public PosDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedClock : IClock
    {
        public long UtcNowMilliseconds => 1_763_300_000_000;

        public long MonotonicMilliseconds => 0;
    }
}
