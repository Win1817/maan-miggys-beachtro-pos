namespace Beachtro.Infrastructure.Tests;

/// <summary>Isolated scratch directory per test; never touches the real %LocalAppData% data roots.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "beachtro-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; temp folder is cleaned by the OS eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
