using Beachtro.Application.Runtime;

namespace Beachtro.Infrastructure.Runtime;

/// <summary>
/// Mutable operational files live outside the install folder, per Windows account
/// (system design §Runtime): data, artifacts, backups, logs, secrets, recovery.
/// Test and Production use different roots so they can never share a ledger.
/// </summary>
public sealed class LocalDataPaths
{
    public LocalDataPaths(string root, RuntimeProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Profile = profile;
    }

    public string Root { get; }
    public RuntimeProfile Profile { get; }

    public string Data => Path.Combine(Root, "data");
    public string Artifacts => Path.Combine(Root, "artifacts");
    public string Backups => Path.Combine(Root, "backups");
    public string Logs => Path.Combine(Root, "logs");
    public string Secrets => Path.Combine(Root, "secrets");
    public string Recovery => Path.Combine(Root, "recovery");

    public string DatabaseFile => Path.Combine(Data, "pos.db");
    public string LockFile => Path.Combine(Data, "app.lock");

    /// <summary>
    /// External number reservation journal (finance design §Number allocation). Lives beside, not
    /// inside, the database so restoring an older database file cannot roll it back.
    /// </summary>
    public string NumberJournalFile => Path.Combine(Data, "numbers.journal");

    /// <summary>External upload-identity journal (sync design §Retry-safe remote creation); same reasoning as the number journal.</summary>
    public string UploadJournalFile => Path.Combine(Data, "uploads.journal");

    public IEnumerable<string> AllDirectories => [Data, Artifacts, Backups, Logs, Secrets, Recovery];

    /// <summary>Default per-user location, e.g. %LocalAppData%\BeachtroPOS-Test.</summary>
    public static LocalDataPaths ForProfile(RuntimeProfile profile)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = profile == RuntimeProfile.Production ? "BeachtroPOS" : "BeachtroPOS-Test";
        return new LocalDataPaths(Path.Combine(baseDir, folder), profile);
    }

    public void EnsureCreated()
    {
        foreach (var dir in AllDirectories)
        {
            Directory.CreateDirectory(dir);
        }
    }
}
