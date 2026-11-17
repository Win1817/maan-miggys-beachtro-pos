using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Beachtro.Application.Ports;
using Beachtro.Application.Reports;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Reports;
using Beachtro.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sync;

/// <summary>Where an artifact lives locally and remotely. Naming is convention, never identity (sync design §Artifact contract).</summary>
public sealed record ArtifactLocation(string RelativePath, string FolderKey, string Name);

/// <summary>The exact bytes of an artifact, or why they cannot be produced.</summary>
public sealed record ArtifactBytes(byte[]? Content, string? Sha256, string? ProblemCode, string? ProblemDetail)
{
    public static ArtifactBytes Problem(string code, string detail) => new(null, null, code, detail);
}

/// <summary>
/// IB-19 artifact materializer (sync design §Materialization protocol). Builds payloads only from
/// immutable sources (stored sale/correction snapshots, stored report runs), writes them by temp file,
/// flush and rename, and only then marks the record Ready. Never overwrites a file whose bytes differ.
/// </summary>
public sealed class ArtifactMaterializer(
    IDbContextFactory<PosDbContext> contextFactory,
    IClock clock,
    WriteGate writeGate,
    LocalDataPaths paths)
{
    public const string TempMarker = ".tmp-";
    public const int SupportedPayloadSchema = 1;

    public static class Codes
    {
        public const string ExistingFileDiffers = "ExistingFileDiffers";
        public const string SourceChecksumMismatch = "SourceChecksumMismatch";
        public const string SourceMissing = "SourceMissing";
        public const string UnsupportedSchema = "UnsupportedSchema";
        public const string RegeneratedBytesDiffer = "RegeneratedBytesDiffer";
        public const string BackupFileMissing = "BackupFileMissing";
        public const string StorageError = "StorageError";
    }

    /// <summary>Materializes up to <paramref name="max"/> intents; returns (Ready, newly held).</summary>
    public async Task<(int Ready, int Held)> MaterializePendingAsync(int max, CancellationToken cancellationToken = default)
    {
        List<ArtifactRecord> intents;
        await using (var read = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var held = read.ArtifactHolds.Where(h => h.ResolvedUtc == null).Select(h => h.ArtifactId);
            intents = await read.ArtifactRecords.AsNoTracking()
                .Where(a => a.State == ArtifactState.Intent && a.Type != ArtifactType.Backup && !held.Contains(a.Id))
                .OrderBy(a => a.CreatedUtc).ThenBy(a => a.Id)
                .Take(max)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var ready = 0;
        var heldCount = 0;
        foreach (var artifact in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await MaterializeAsync(artifact, cancellationToken).ConfigureAwait(false))
            {
                ready++;
            }
            else
            {
                heldCount++;
            }
        }

        return (ready, heldCount);
    }

    /// <summary>
    /// Makes sure a Ready artifact's file is present with its recorded bytes. A missing or damaged file is
    /// regenerated only when the source reproduces the recorded SHA-256 (damaged copies are kept as evidence).
    /// </summary>
    public async Task<ArtifactBytes> ReadReadyAsync(ArtifactRecord artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.State != ArtifactState.Ready || artifact.RelativePath is null || artifact.Sha256 is null)
        {
            return ArtifactBytes.Problem(Codes.SourceMissing, "The artifact has no file yet.");
        }

        var full = FullPath(artifact.RelativePath);
        if (File.Exists(full))
        {
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            if (Sha(bytes) == artifact.Sha256)
            {
                return new ArtifactBytes(bytes, artifact.Sha256, null, null);
            }
        }

        if (artifact.Type == ArtifactType.Backup)
        {
            // A backup cannot be rebuilt from a newer database under the old identity.
            return ArtifactBytes.Problem(Codes.BackupFileMissing, "The backup package is missing or damaged and cannot be recreated under the same identity.");
        }

        ArtifactBytes rebuilt;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            rebuilt = await BuildAsync(db, artifact, cancellationToken).ConfigureAwait(false);
        }

        if (rebuilt.Content is null)
        {
            return rebuilt;
        }

        if (rebuilt.Sha256 != artifact.Sha256)
        {
            return ArtifactBytes.Problem(Codes.RegeneratedBytesDiffer, "Regenerated bytes do not match the recorded checksum; the artifact is held, not replaced.");
        }

        if (File.Exists(full))
        {
            var stamp = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMilliseconds).ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
            File.Move(full, full + ".damaged-" + stamp);
        }

        var problem = WriteAtomically(full, rebuilt.Content, rebuilt.Sha256!);
        return problem is null ? rebuilt : ArtifactBytes.Problem(problem, "The regenerated file could not be written.");
    }

    /// <summary>Removes temp files left by a crash mid-write; no record ever points at a temp file.</summary>
    public int CleanupTempFiles()
    {
        var root = Path.Combine(paths.Root, "artifacts");
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*" + TempMarker + "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
                // In use by a writer of this process; the next cycle retries.
            }
        }

        return removed;
    }

    public static async Task<ArtifactLocation?> LocateAsync(PosDbContext db, ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(artifact);
        switch (artifact.Type)
        {
            case ArtifactType.SaleJson:
            {
                var date = await db.Orders.Where(o => o.Id == artifact.OrderId).Select(o => o.BusinessDate).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return date is null ? null : Transaction(date, $"sale_{artifact.SourceId:D}_v{artifact.PayloadSchemaVersion}.json");
            }

            case ArtifactType.CorrectionJson:
            {
                var date = await db.Corrections.Where(c => c.Id == artifact.CorrectionId).Select(c => c.BusinessDate).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return date is null ? null : Transaction(date, $"correction_{artifact.SourceId:D}_v{artifact.PayloadSchemaVersion}.json");
            }

            case ArtifactType.ReportCsv or ArtifactType.ReportPdf:
            {
                var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == artifact.ReportRunId, cancellationToken).ConfigureAwait(false);
                if (run is null)
                {
                    return null;
                }

                var format = artifact.Type == ArtifactType.ReportCsv ? ReportFormat.Csv : ReportFormat.Pdf;
                var name = ReportName(run, format);
                var folder = run.Type switch
                {
                    ReportType.DailySales => $"Daily Reports/{run.RangeStartDate[..4]}/{run.RangeStartDate[5..7]}",
                    ReportType.MonthlySales => $"Monthly Reports/{run.RangeStartDate[..4]}",
                    _ => $"Exports/{run.RangeStartDate[..4]}/{run.RangeStartDate[5..7]}",
                };
                return new ArtifactLocation($"artifacts/{Slug(folder)}/{name}", folder, name);
            }

            case ArtifactType.Backup:
            {
                var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == artifact.BackupId, cancellationToken).ConfigureAwait(false);
                if (backup is null)
                {
                    return null;
                }

                var utc = DateTimeOffset.FromUnixTimeMilliseconds(backup.CreatedUtc).UtcDateTime;
                return new ArtifactLocation(backup.RelativePath, $"Database Backups/{utc:yyyy}/{utc:MM}", Path.GetFileName(backup.RelativePath));
            }

            default:
                return null;
        }
    }

    /// <summary>The report file name: human-readable range, revision and snapshot hash, plus the run ID so names never collide.</summary>
    public static string ReportName(ReportRun run, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(run);
        var rendered = ReportService.FileStem(run);
        return $"{rendered}_{run.Id:N}" + (format == ReportFormat.Csv ? ".csv" : ".pdf");
    }

    // ---------------------------------------------------------------- internals

    private async Task<bool> MaterializeAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        ArtifactBytes payload;
        ArtifactLocation? location;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            location = await LocateAsync(db, artifact, cancellationToken).ConfigureAwait(false);
            payload = location is null
                ? ArtifactBytes.Problem(Codes.SourceMissing, "The source record for this artifact was not found.")
                : await BuildAsync(db, artifact, cancellationToken).ConfigureAwait(false);
        }

        if (payload.Content is null)
        {
            await HoldAsync(artifact.Id, payload.ProblemCode!, payload.ProblemDetail!, cancellationToken).ConfigureAwait(false);
            return false;
        }

        string? problem;
        try
        {
            problem = WriteAtomically(FullPath(location!.RelativePath), payload.Content, payload.Sha256!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full or locked: the intent stays (shown as awaiting a file) and the next cycle retries.
            return false;
        }

        if (problem is not null)
        {
            await HoldAsync(artifact.Id, problem, "A different file already exists at this artifact's path; it was kept and not overwritten.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var write = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await write.ArtifactRecords.SingleAsync(a => a.Id == artifact.Id, cancellationToken).ConfigureAwait(false);
        if (record.State == ArtifactState.Intent)
        {
            record.RelativePath = location.RelativePath;
            record.Sha256 = payload.Sha256;
            record.SizeBytes = payload.Content.LongLength;
            record.MaterializedUtc = clock.UtcNowMilliseconds;
            record.State = ArtifactState.Ready;
            await write.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Builds bytes from the immutable source only, never from a live query.</summary>
    private static async Task<ArtifactBytes> BuildAsync(PosDbContext db, ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        if (artifact.PayloadSchemaVersion != SupportedPayloadSchema)
        {
            return ArtifactBytes.Problem(Codes.UnsupportedSchema, $"Payload schema {artifact.PayloadSchemaVersion} is not supported by this version.");
        }

        switch (artifact.Type)
        {
            case ArtifactType.SaleJson:
            {
                var source = await db.Orders.Where(o => o.Id == artifact.OrderId).Select(o => new { o.SnapshotJson, o.SnapshotSha256 }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return source is null ? ArtifactBytes.Problem(Codes.SourceMissing, "Sale not found.") : Snapshot(source.SnapshotJson, source.SnapshotSha256);
            }

            case ArtifactType.CorrectionJson:
            {
                var source = await db.Corrections.Where(c => c.Id == artifact.CorrectionId).Select(c => new { c.SnapshotJson, c.SnapshotSha256 }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return source is null ? ArtifactBytes.Problem(Codes.SourceMissing, "Correction not found.") : Snapshot(source.SnapshotJson, source.SnapshotSha256);
            }

            case ArtifactType.ReportCsv or ArtifactType.ReportPdf:
            {
                var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == artifact.ReportRunId, cancellationToken).ConfigureAwait(false);
                if (run is null)
                {
                    return ArtifactBytes.Problem(Codes.SourceMissing, "Report run not found.");
                }

                var file = ReportService.Render(run, artifact.Type == ArtifactType.ReportCsv ? ReportFormat.Csv : ReportFormat.Pdf);
                return new ArtifactBytes(file.Content, Sha(file.Content), null, null);
            }

            default:
                return ArtifactBytes.Problem(Codes.SourceMissing, "This artifact type is created complete and cannot be rebuilt.");
        }
    }

    private static ArtifactBytes Snapshot(string json, string recordedSha)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha = Sha(bytes);
        return sha == recordedSha
            ? new ArtifactBytes(bytes, sha, null, null)
            : ArtifactBytes.Problem(Codes.SourceChecksumMismatch, "The stored snapshot does not match its recorded checksum.");
    }

    private async Task HoldAsync(Guid artifactId, string code, string detail, CancellationToken cancellationToken)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        var hold = await db.ArtifactHolds.SingleOrDefaultAsync(h => h.ArtifactId == artifactId, cancellationToken).ConfigureAwait(false);
        if (hold is null)
        {
            db.ArtifactHolds.Add(new ArtifactHold { ArtifactId = artifactId, Code = code, Detail = detail, DetectedUtc = now });
        }
        else
        {
            hold.Code = code;
            hold.Detail = detail;
            hold.DetectedUtc = now;
            hold.ResolvedUtc = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns null on success (including an identical file already present), or a problem code.</summary>
    private static string? WriteAtomically(string full, byte[] content, string sha)
    {
        if (File.Exists(full))
        {
            return Sha(File.ReadAllBytes(full)) == sha ? null : Codes.ExistingFileDiffers;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + TempMarker + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, full, overwrite: false);
            return null;
        }
        catch (IOException) when (File.Exists(full))
        {
            // Another writer finished first: accept only identical bytes.
            File.Delete(temp);
            return Sha(File.ReadAllBytes(full)) == sha ? null : Codes.ExistingFileDiffers;
        }
    }

    private string FullPath(string relative)
    {
        var root = Path.GetFullPath(paths.Root);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new InvalidOperationException("Artifact path escapes the data folder.");
    }

    private static ArtifactLocation Transaction(string businessDate, string name)
    {
        var folder = $"Transactions/{businessDate[..4]}/{businessDate[5..7]}";
        return new ArtifactLocation($"artifacts/{Slug(folder)}/{name}", folder, name);
    }

    private static string Slug(string folder) => folder.ToLowerInvariant().Replace(' ', '-');

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
