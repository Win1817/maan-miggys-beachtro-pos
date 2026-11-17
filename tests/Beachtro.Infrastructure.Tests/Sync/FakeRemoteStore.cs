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

    /// <summary>Number of upcoming reads that see nothing (eventual consistency).</summary>
    public int HiddenReads { get; set; }

    public Task<string> GenerateIdAsync(CancellationToken cancellationToken) =>
        Task.FromResult("f_" + (++_next).ToString(CultureInfo.InvariantCulture));

    public Task CreateAsync(string id, string folderKey, string name, byte[] content, IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken)
    {
        CreateCalls++;
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
        if (HiddenReads > 0)
        {
            HiddenReads--;
            return Task.FromResult<RemoteObject?>(null);
        }

        return Task.FromResult(Files.TryGetValue(id, out var f)
            ? new RemoteObject(id, f.Folder, f.Name, f.Bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(f.Bytes)), f.Properties)
            : null);
    }
}

public sealed class FakeRemoteFactory(IRemoteStore store) : IRemoteStoreFactory
{
    public IRemoteStore? Create(CloudStorageSettings settings) => store;
}
