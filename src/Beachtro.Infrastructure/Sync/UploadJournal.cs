using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Beachtro.Infrastructure.Sync;

/// <summary>One remote identity allocation: this artifact (logical key + bytes) goes to this remote ID in this binding.</summary>
public sealed record UploadIdentity(Guid BindingId, string LogicalKey, string Sha256, string RemoteId, long AllocatedUtc, string PreviousHash, string Hash);

public sealed record UploadJournalState(bool Exists, bool Trustworthy, IReadOnlyList<UploadIdentity> Records, string? Problem)
{
    public string LastHash => Records.Count == 0 ? UploadJournal.Genesis : Records[^1].Hash;

    /// <summary>The latest identity allocated for exactly this artifact content in this binding.</summary>
    public UploadIdentity? Find(Guid bindingId, string logicalKey, string sha256) =>
        Records.LastOrDefault(r => r.BindingId == bindingId && r.LogicalKey == logicalKey && r.Sha256 == sha256);
}

/// <summary>
/// External upload-identity journal (sync design §Retry-safe remote creation). Lives beside the
/// database, not inside it, so restoring an older database cannot forget a remote ID that may already
/// have been sent. Line: <c>v1|binding|logicalKey|sha256|remoteId|utc|previousHash|hash</c>, appended
/// with write-through and flushed before the remote create is attempted.
/// </summary>
public sealed class UploadJournal(string path)
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Version = "v1";

    public string Path { get; } = path;

    public UploadJournalState Read()
    {
        if (!File.Exists(Path))
        {
            return new UploadJournalState(false, true, [], null);
        }

        var lines = File.ReadAllText(Path, Encoding.UTF8).Split('\n');
        var records = new List<UploadIdentity>();
        var previous = Genesis;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var record = Parse(lines[i]);
            if (record is null || record.PreviousHash != previous)
            {
                return new UploadJournalState(true, false, records, $"Upload journal line {i + 1} is damaged or out of chain.");
            }

            records.Add(record);
            previous = record.Hash;
        }

        // A torn final line was never acknowledged, so its ID was never sent; the valid prefix stands.
        return new UploadJournalState(true, true, records, lines[^1].Length > 0 ? "Torn final line ignored." : null);
    }

    public UploadIdentity Append(UploadJournalState current, Guid bindingId, string logicalKey, string sha256, string remoteId, long utc)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.Trustworthy)
        {
            throw new InvalidOperationException("The upload journal is damaged.");
        }

        if (logicalKey.Contains('|', StringComparison.Ordinal) || remoteId.Contains('|', StringComparison.Ordinal))
        {
            throw new ArgumentException("Journal fields cannot contain '|'.");
        }

        var body = Body(bindingId, logicalKey, sha256, remoteId, utc, current.LastHash);
        var hash = HashOf(body);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // A torn tail from an earlier crash is cut off first so the new line starts clean.
        var prefix = current.Problem is not null && File.Exists(Path) ? Encoding.UTF8.GetString(File.ReadAllBytes(Path)) : null;
        if (prefix is not null)
        {
            var cut = prefix.LastIndexOf('\n') + 1;
            using var fix = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            fix.SetLength(Encoding.UTF8.GetByteCount(prefix[..cut]));
            fix.Flush(flushToDisk: true);
        }

        using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            stream.Write(Encoding.UTF8.GetBytes($"{body}|{hash}\n"));
            stream.Flush(flushToDisk: true);
        }

        return new UploadIdentity(bindingId, logicalKey, sha256, remoteId, utc, current.LastHash, hash);
    }

    private static UploadIdentity? Parse(string line)
    {
        var f = line.Split('|');
        if (f.Length != 8 || f[0] != Version
            || !Guid.TryParseExact(f[1], "D", out var binding)
            || !long.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var utc))
        {
            return null;
        }

        return HashOf(string.Join('|', f[..7])) == f[7] ? new UploadIdentity(binding, f[2], f[3], f[4], utc, f[6], f[7]) : null;
    }

    private static string Body(Guid binding, string key, string sha, string remoteId, long utc, string previous) =>
        string.Join('|', Version, binding.ToString("D"), key, sha, remoteId, utc.ToString(CultureInfo.InvariantCulture), previous);

    private static string HashOf(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
