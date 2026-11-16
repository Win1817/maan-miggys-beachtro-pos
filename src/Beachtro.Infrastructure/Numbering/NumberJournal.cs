using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Beachtro.Infrastructure.Numbering;

/// <summary>One durable reservation. A record with an empty RequestId is an owner-approved checkpoint.</summary>
public sealed record JournalRecord(Guid InstallationId, string SeriesKey, int Sequence, Guid RequestId, long ReservedUtc, string PreviousHash, string Hash);

public sealed record JournalState(bool Exists, bool Trustworthy, bool TornTail, IReadOnlyList<JournalRecord> Records, string? Problem)
{
    public string LastHash => Records.Count == 0 ? NumberJournal.Genesis : Records[^1].Hash;

    public int HighWater(string seriesKey) =>
        Records.Where(r => r.SeriesKey == seriesKey).Select(r => r.Sequence).DefaultIfEmpty(0).Max();

    public IEnumerable<string> SeriesKeys => Records.Select(r => r.SeriesKey).Distinct(StringComparer.Ordinal);
}

/// <summary>
/// External number reservation journal (finance design §Number allocation and restore safety).
/// One line per reservation: <c>v1|installation|series|sequence|request|utc|previousHash|hash</c>,
/// where hash = SHA-256 over the first seven fields. Lines are appended with write-through and an
/// explicit flush before the database transaction starts. A missing final newline marks a torn
/// write; a broken line or chain anywhere else makes the journal untrustworthy.
/// </summary>
public sealed class NumberJournal(string path)
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Version = "v1";

    public string Path { get; } = path;

    public JournalState Read()
    {
        if (!File.Exists(Path))
        {
            return new JournalState(false, true, false, [], null);
        }

        var text = File.ReadAllText(Path, Encoding.UTF8);
        var lines = text.Split('\n');
        var complete = lines[..^1];
        var tornTail = lines[^1].Length > 0;

        var records = new List<JournalRecord>(complete.Length);
        var previous = Genesis;
        for (var i = 0; i < complete.Length; i++)
        {
            var record = Parse(complete[i]);
            if (record is null || record.PreviousHash != previous)
            {
                return new JournalState(true, false, tornTail, records, $"Journal line {i + 1} is damaged or out of chain.");
            }

            records.Add(record);
            previous = record.Hash;
        }

        return new JournalState(true, true, tornTail, records, null);
    }

    /// <summary>Appends and flushes to stable storage; throws IOException if durability cannot be confirmed.</summary>
    public JournalRecord Append(JournalState current, Guid installationId, string seriesKey, int sequence, Guid requestId, long reservedUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.Trustworthy || current.TornTail)
        {
            throw new InvalidOperationException("The journal must be reconciled before appending.");
        }

        var body = Body(installationId, seriesKey, sequence, requestId, reservedUtc, current.LastHash);
        var hash = HashOf(body);
        var line = Encoding.UTF8.GetBytes($"{body}|{hash}\n");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            stream.Write(line);
            stream.Flush(flushToDisk: true);
        }

        return new JournalRecord(installationId, seriesKey, sequence, requestId, reservedUtc, current.LastHash, hash);
    }

    /// <summary>
    /// Rewrites the journal with only its valid records (dropping a torn final line or everything
    /// after the first damaged line), after copying the original to <paramref name="evidenceFolder"/>.
    /// </summary>
    public void RewriteValidPrefix(JournalState state, string evidenceFolder)
    {
        ArgumentNullException.ThrowIfNull(state);
        PreserveEvidence(evidenceFolder);
        var temp = Path + ".rewrite";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            foreach (var r in state.Records)
            {
                stream.Write(Encoding.UTF8.GetBytes($"{Body(r.InstallationId, r.SeriesKey, r.Sequence, r.RequestId, r.ReservedUtc, r.PreviousHash)}|{r.Hash}\n"));
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>Moves an untrustworthy journal aside as evidence so a fresh chain can start.</summary>
    public void Archive(string evidenceFolder)
    {
        PreserveEvidence(evidenceFolder);
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    private void PreserveEvidence(string evidenceFolder)
    {
        if (!File.Exists(Path))
        {
            return;
        }

        Directory.CreateDirectory(evidenceFolder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        File.Copy(Path, System.IO.Path.Combine(evidenceFolder, $"numbers.journal.{stamp}.evidence"), overwrite: false);
    }

    private static JournalRecord? Parse(string line)
    {
        var f = line.Split('|');
        if (f.Length != 8 || f[0] != Version
            || !Guid.TryParseExact(f[1], "D", out var installation)
            || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || !Guid.TryParseExact(f[4], "D", out var request)
            || !long.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var utc))
        {
            return null;
        }

        var body = string.Join('|', f[..7]);
        return HashOf(body) == f[7] ? new JournalRecord(installation, f[2], sequence, request, utc, f[6], f[7]) : null;
    }

    private static string Body(Guid installation, string series, int sequence, Guid request, long utc, string previous) =>
        string.Join('|', Version, installation.ToString("D"), series, sequence.ToString(CultureInfo.InvariantCulture),
            request.ToString("D"), utc.ToString(CultureInfo.InvariantCulture), previous);

    private static string HashOf(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
