using System.Security.Cryptography;
using System.Text;

namespace Beachtro.Infrastructure.Runtime;

/// <summary>
/// One writer per data directory: an OS named mutex plus an exclusively opened lock file
/// (system design §Runtime). A second launch cannot open a second writer.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly FileStream _lockFile;
    private bool _disposed;

    private SingleInstanceLock(Mutex mutex, FileStream lockFile)
    {
        _mutex = mutex;
        _lockFile = lockFile;
    }

    /// <summary>Returns null when another process already holds the data directory.</summary>
    public static SingleInstanceLock? TryAcquire(LocalDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.Data);

        var mutex = new Mutex(initiallyOwned: false, MutexName(paths.Root));
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Previous holder crashed; ownership transfers to us. Correctness relies on
            // request idempotency and recovery, not on clean shutdown.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        try
        {
            var stream = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceLock(mutex, stream);
        }
        catch (IOException)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            return null;
        }
    }

    internal static string MutexName(string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $@"Local\BeachtroPOS.{hash}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockFile.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
