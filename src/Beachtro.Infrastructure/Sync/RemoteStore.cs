using System.Security.Cryptography;
using System.Text.Json;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Runtime;

namespace Beachtro.Infrastructure.Sync;

/// <summary>What the remote side reports about one file. ContentSha256 is computed by the store, not echoed from the client.</summary>
public sealed record RemoteObject(string Id, string FolderKey, string Name, long Size, string? ContentSha256, IReadOnlyDictionary<string, string> Properties);

public enum RemoteErrorKind { Transient, RateLimited, Unauthorized, PermissionDenied, QuotaExceeded, Conflict, Permanent }

/// <summary>A remote failure with a safe message (no tokens, no payload).</summary>
public sealed class RemoteStoreException : Exception
{
    public RemoteStoreException()
    {
    }

    public RemoteStoreException(string message)
        : base(message)
    {
    }

    public RemoteStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RemoteStoreException(RemoteErrorKind kind, string message, int? httpCode = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        HttpCode = httpCode;
        RetryAfter = retryAfter;
    }

    public RemoteErrorKind Kind { get; } = RemoteErrorKind.Transient;
    public int? HttpCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Remote destination port (sync design §Retry-safe remote creation). Identity is allocated before the
/// first send so every retry targets the same file; creating an ID that already exists is a Conflict.
/// </summary>
public interface IRemoteStore
{
    Task<string> GenerateIdAsync(CancellationToken cancellationToken);

    Task CreateAsync(string id, string folderKey, string name, byte[] content, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken);

    /// <summary>Null when no file has that ID (which never proves a create was not sent).</summary>
    Task<RemoteObject?> GetAsync(string id, CancellationToken cancellationToken);
}

public interface IRemoteStoreFactory
{
    /// <summary>The store for the bound destination, or null when the provider is not available in this build.</summary>
    IRemoteStore? Create(CloudStorageSettings settings);
}

public static class RemoteProviders
{
    /// <summary>A local folder that behaves like Drive. Test profile only (POS-DEC-007).</summary>
    public const string SimulatedFolder = "SimulatedFolder";
    public const string GoogleDrive = "GoogleDrive";
}

public sealed class RemoteStoreFactory(LocalDataPaths paths) : IRemoteStoreFactory
{
    public IRemoteStore? Create(CloudStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Provider == RemoteProviders.SimulatedFolder && paths.Profile == Application.Runtime.RuntimeProfile.Test
            ? new SimulatedDriveStore(Path.Combine(paths.Root, settings.RootFolderId ?? "simulated-drive"))
            : null;
    }
}

/// <summary>
/// Simulated destination: each file is <c>{id}.bin</c> plus <c>{id}.json</c> metadata, stored by ID like
/// Drive, so duplicate names never overwrite. Writes go through a temp file and rename.
/// </summary>
public sealed class SimulatedDriveStore(string root) : IRemoteStore
{
    public string Root { get; } = root;

    public Task<string> GenerateIdAsync(CancellationToken cancellationToken) =>
        Task.FromResult("sim_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    public async Task CreateAsync(string id, string folderKey, string name, byte[] content, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(Root);
        var bin = Path.Combine(Root, Safe(id) + ".bin");
        var meta = Path.Combine(Root, Safe(id) + ".json");
        if (File.Exists(meta))
        {
            throw new RemoteStoreException(RemoteErrorKind.Conflict, "A file with this ID already exists.", 409);
        }

        await File.WriteAllBytesAsync(bin + ".tmp", content, cancellationToken).ConfigureAwait(false);
        File.Move(bin + ".tmp", bin, overwrite: true);
        var json = JsonSerializer.Serialize(new Meta(folderKey, name, properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)));
        await File.WriteAllTextAsync(meta + ".tmp", json, cancellationToken).ConfigureAwait(false);
        File.Move(meta + ".tmp", meta);
    }

    public async Task<RemoteObject?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var bin = Path.Combine(Root, Safe(id) + ".bin");
        var meta = Path.Combine(Root, Safe(id) + ".json");
        if (!File.Exists(meta) || !File.Exists(bin))
        {
            return null;
        }

        var m = JsonSerializer.Deserialize<Meta>(await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false))!;
        await using var stream = File.OpenRead(bin);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return new RemoteObject(id, m.FolderKey, m.Name, stream.Length, sha, m.Properties);
    }

    private static string Safe(string id) =>
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? id : throw new RemoteStoreException(RemoteErrorKind.Permanent, "Invalid remote ID.");

    private sealed record Meta(string FolderKey, string Name, Dictionary<string, string> Properties);
}
