using Microsoft.Data.Sqlite;

namespace Beachtro.Infrastructure.Persistence;

/// <summary>Classifies SQLite failures that mean "try again later", not a defect (IB-26).</summary>
public static class SqliteErrors
{
    private const int Busy = 5;
    private const int Locked = 6;

    /// <summary>True when another connection held the database past the busy timeout (SQLITE_BUSY/LOCKED).</summary>
    public static bool IsBusy(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqliteException { SqliteErrorCode: Busy or Locked })
            {
                return true;
            }
        }

        return false;
    }

    public const string BusyMessage =
        "The database is busy (another program is using it). Nothing was saved; any number set aside is skipped, never reused. Try again.";
}
