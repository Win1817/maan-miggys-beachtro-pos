using System.Globalization;
using System.Security.Cryptography;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Sync;

namespace Beachtro.Infrastructure.Tests.Sync;

/// <summary>
/// In-memory Drive stand-in (test plan: the fake must support a create that succeeded before an error
/// was thrown, not only failures before the request). Fault switches are one-shot.
/// </summary>
public sealed class FakeRemoteStore : IRemoteStore
{
    private int _next;

    public Dictionary<string, (string Folder, string Name, byte[] Bytes, Dictionary<string, string> Properties)> Files { get; } = new(StringComparer.Ordinal);

    public int CreateCalls { get; private set; }

    /// <summary>Thrown by the next create before anything is stored.</summary>
    public RemoteStoreException? FailBeforeCreate { get; set; }

    /// <summary>Thrown by the next create after the file was stored (lost acknowledgement).</summary>
    public RemoteStoreException? FailAfterCreate { get; set; }

    /// <summary>While true, every call fails as a network outage (not one-shot).</summary>
    public bool Offline { get; set; }

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new RemoteStoreException(RemoteErrorKind.Transient, "offline", null);
        }
    }

    /// <summary>Number of upcoming reads that see nothing (eventual consistency).</summary>
    public int HiddenReads { get; set; }

    public Task<string> GenerateIdAsync(CancellationToken cancellationToken) =>
        Task.FromResult("f_" + (++_next).ToString(CultureInfo.InvariantCulture));

    public Task CreateAsync(string id, string folderKey, string name, byte[] content, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken)
    {
        CreateCalls++;
        ThrowIfOffline();
        if (FailBeforeCreate is { } before)
        {
            FailBeforeCreate = null;
            throw before;
        }

        if (Files.ContainsKey(id))
        {
            throw new RemoteStoreException(RemoteErrorKind.Conflict, "exists", 409);
        }

        Files[id] = (folderKey, name, content.ToArray(), properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        if (FailAfterCreate is { } after)
        {
            FailAfterCreate = null;
            throw after;
        }

        return Task.CompletedTask;
    }

    public Task<RemoteObject?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        if (HiddenReads > 0)
        {
            HiddenReads--;
            return Task.FromResult<RemoteObject?>(null);
        }

        return Task.FromResult(Files.TryGetValue(id, out var f)
            ? new RemoteObject(id, f.Folder, f.Name, f.Bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(f.Bytes)), f.Properties)
            : null);
    }

    /// <summary>Number of upcoming searches that see nothing (Drive search lag).</summary>
    public int HiddenSearches { get; set; }

    public async Task<IReadOnlyList<RemoteObject>> FindAsync(string logicalKey, CancellationToken cancellationToken)
    {
        if (HiddenSearches > 0)
        {
            HiddenSearches--;
            return [];
        }

        var found = new List<RemoteObject>();
        foreach (var id in Files.Where(f => f.Value.Properties.GetValueOrDefault("logicalKey") == logicalKey).Select(f => f.Key).ToArray())
        {
            found.Add((await GetAsync(id, cancellationToken))!);
        }

        return found;
    }

    public int CountFor(string logicalKey) => Files.Values.Count(f => f.Properties.GetValueOrDefault("logicalKey") == logicalKey);
}

public sealed class FakeRemoteFactory(IRemoteStore store) : IRemoteStoreFactory
{
    public IRemoteStore? Create(CloudStorageSettings settings) => store;
}
