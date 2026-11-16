using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Beachtro.Infrastructure.Persistence;

public static class PersistenceOptions
{
    public static string ConnectionString(string databaseFile) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();

    public static void Configure(DbContextOptionsBuilder builder, string databaseFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .UseSqlite(ConnectionString(databaseFile), sqlite => sqlite.MigrationsAssembly(typeof(PosDbContext).Assembly.FullName))
            .AddInterceptors(new SqlitePragmaInterceptor());
    }
}

/// <summary>Design-time factory for dotnet-ef. Uses a throwaway path; never a real data folder.</summary>
public sealed class PosDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PosDbContext>();
        PersistenceOptions.Configure(builder, Path.Combine(Path.GetTempPath(), "beachtro-design-time.db"));
        return new PosDbContext(builder.Options);
    }
}
