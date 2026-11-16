using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>
/// Connection-scoped pragmas applied on every open (database design §Physical conventions).
/// journal_mode=WAL is database-scoped and set once by <see cref="DatabaseInitializer"/>.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public const int BusyTimeoutMilliseconds = 5000;

    internal const string Pragmas =
        "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void Apply(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }
}
