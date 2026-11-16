using System.Globalization;
using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Recovery;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Numbering;

/// <summary>A number reserved in the journal, to be recorded in the same transaction as the sale.</summary>
public sealed record ReservedNumber(string SeriesKey, string BusinessDate, string Prefix, int Sequence)
{
    public string TransactionNumber => $"{SeriesKey}-{Sequence:D6}";
}

/// <summary>
/// Transaction numbering: next = max(database high-water, journal high-water) + 1, journaled and
/// flushed before the database write. A reservation stays consumed even if the sale rolls back
/// (an audited gap); numbers are never reused (BRD §8; finance design; IB-08).
/// </summary>
public sealed class NumberSeriesService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IClock clock,
    WriteGate writeGate,
    LocalDataPaths paths) : INumberSeriesService
{
    public const string Prefix = "MB";
    public const int MaxSequence = 999_999;

    public NumberJournal Journal { get; } = new(paths.NumberJournalFile);

    public static string SeriesKeyFor(string businessDate) => $"{Prefix}-{businessDate.Replace("-", string.Empty, StringComparison.Ordinal)}";

    /// <summary>
    /// Reserves the next number. Call inside the write gate and before the sale transaction; then
    /// record it with <see cref="ApplyReservation"/> inside that transaction.
    /// </summary>
    internal async Task<Result<ReservedNumber>> ReserveAsync(PosDbContext db, string businessDate, Guid requestId, CancellationToken cancellationToken)
    {
        var key = SeriesKeyFor(businessDate);
        var state = Journal.Read();
        if (!state.Trustworthy || state.TornTail)
        {
            return Result<ReservedNumber>.Fail(ResultStatus.RecoveryRequired, "The number journal needs reconciliation before selling. Restart the application or contact the owner.");
        }

        var installation = await db.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Exists && await db.NumberSeries.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<ReservedNumber>.Fail(ResultStatus.RecoveryRequired, "The number journal is missing. Selling is blocked until the owner resolves the number series.");
        }

        if (state.Records.Count > 0 && state.Records[^1].InstallationId != installation.InstallationId)
        {
            return Result<ReservedNumber>.Fail(ResultStatus.RecoveryRequired, "The number journal belongs to another installation.");
        }

        var series = await db.NumberSeries.AsNoTracking().SingleOrDefaultAsync(s => s.SeriesKey == key, cancellationToken).ConfigureAwait(false);
        if (series?.Status == NumberSeriesStatus.Blocked)
        {
            return Result<ReservedNumber>.Fail(ResultStatus.RecoveryRequired, $"Number series {key} is on hold pending recovery reconciliation.");
        }

        var next = Math.Max(series?.LastReservedSequence ?? 0, state.HighWater(key)) + 1;
        if (series?.Status == NumberSeriesStatus.Exhausted || next > MaxSequence)
        {
            return Result<ReservedNumber>.Fail(ResultStatus.Conflict, $"Number series {key} is exhausted; the format is never widened silently.");
        }

        try
        {
            Journal.Append(state, installation.InstallationId, key, next, requestId, clock.UtcNowMilliseconds);
        }
        catch (IOException)
        {
            return Result<ReservedNumber>.Fail(ResultStatus.StorageUnavailable, "The number journal could not be written safely. The sale was not completed.");
        }
        catch (UnauthorizedAccessException)
        {
            return Result<ReservedNumber>.Fail(ResultStatus.StorageUnavailable, "The number journal could not be written safely. The sale was not completed.");
        }

        return Result<ReservedNumber>.Ok(new ReservedNumber(key, businessDate, Prefix, next));
    }

    /// <summary>Raises the database high-water mark inside the sale transaction (never lowers it).</summary>
    internal static async Task ApplyReservationAsync(PosDbContext db, ReservedNumber reserved, long nowUtc, CancellationToken cancellationToken)
    {
        var series = await db.NumberSeries.SingleOrDefaultAsync(s => s.SeriesKey == reserved.SeriesKey, cancellationToken).ConfigureAwait(false);
        if (series is null)
        {
            db.NumberSeries.Add(new NumberSeries
            {
                SeriesKey = reserved.SeriesKey,
                BusinessDate = reserved.BusinessDate,
                Prefix = reserved.Prefix,
                LastReservedSequence = reserved.Sequence,
                Status = NumberSeriesStatus.Ready,
                LastReconciledUtc = nowUtc,
            });
        }
        else if (series.LastReservedSequence < reserved.Sequence)
        {
            series.LastReservedSequence = reserved.Sequence;
            series.Version++;
        }
    }

    public async Task<NumberReconciliation> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReconcileAsync(db, "startup", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings every series up to the journal high-water mark. If the journal is damaged, missing
    /// while series exist, or belongs to another installation, all Ready series are blocked: their
    /// high-water mark cannot be proven.
    /// </summary>
    internal async Task<NumberReconciliation> ReconcileAsync(PosDbContext db, string context, CancellationToken cancellationToken)
    {
        var now = clock.UtcNowMilliseconds;
        var installation = await db.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var state = Journal.Read();
        var repaired = false;

        if (state.Trustworthy && state.TornTail)
        {
            Journal.RewriteValidPrefix(state, paths.Recovery);
            state = Journal.Read();
            repaired = true;
            AuditRecorder.Record(db, now, null, AuditRecorder.SystemActorName, "Numbering.JournalTornTailRepaired", "NumberJournal", null, null,
                new { context, validRecords = state.Records.Count });
        }

        var series = await db.NumberSeries.ToListAsync(cancellationToken).ConfigureAwait(false);
        string? unprovable = null;
        if (!state.Trustworthy)
        {
            unprovable = state.Problem ?? "The number journal is damaged.";
        }
        else if (!state.Exists && series.Count > 0)
        {
            unprovable = "The number journal is missing while number series exist.";
        }
        else if (state.Records.Any(r => r.InstallationId != installation.InstallationId))
        {
            unprovable = "The number journal contains records from another installation.";
        }

        if (unprovable is not null)
        {
            foreach (var s in series.Where(s => s.Status == NumberSeriesStatus.Ready))
            {
                s.Status = NumberSeriesStatus.Blocked;
                s.EvidenceRef = unprovable;
                s.Version++;
            }

            AuditRecorder.Record(db, now, null, AuditRecorder.SystemActorName, "Numbering.SeriesBlocked", "NumberSeries", null, unprovable, new { context });
        }
        else
        {
            foreach (var key in state.SeriesKeys)
            {
                var high = state.HighWater(key);
                var row = series.SingleOrDefault(s => s.SeriesKey == key);
                if (row is null)
                {
                    db.NumberSeries.Add(new NumberSeries
                    {
                        SeriesKey = key,
                        BusinessDate = BusinessDateOf(key),
                        Prefix = Prefix,
                        LastReservedSequence = high,
                        Status = NumberSeriesStatus.Ready,
                        LastReconciledUtc = now,
                    });
                }
                else if (row.LastReservedSequence < high)
                {
                    row.LastReservedSequence = high;
                    row.LastReconciledUtc = now;
                    row.Version++;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var blocked = await db.NumberSeries.AsNoTracking().Where(s => s.Status == NumberSeriesStatus.Blocked)
            .Select(s => new SeriesHold(s.SeriesKey, s.LastReservedSequence, s.EvidenceRef ?? "Blocked"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new NumberReconciliation(blocked, repaired);
    }

    public async Task<Result<Unit>> ResolveHoldAsync(UserSession session, string seriesKey, int provenHighWater, string evidence, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Application.Security.Capabilities.BackupRestore, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(evidence, "Evidence", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var series = await db.NumberSeries.SingleOrDefaultAsync(s => s.SeriesKey == seriesKey, cancellationToken).ConfigureAwait(false);
        if (series is null || series.Status != NumberSeriesStatus.Blocked)
        {
            return Result.Fail(ResultStatus.NotFound, "That number series is not on hold.");
        }

        var state = Journal.Read();
        var known = Math.Max(series.LastReservedSequence, state.Trustworthy ? state.HighWater(seriesKey) : 0);
        if (provenHighWater < known || provenHighWater > MaxSequence)
        {
            return Result.Fail(ResultStatus.Validation, $"The proven high-water mark must be at least {known} (the highest number already recorded).");
        }

        // A damaged or missing journal is archived as evidence and a fresh chain starts at the checkpoint.
        if (!state.Trustworthy || state.TornTail || !state.Exists)
        {
            Journal.Archive(paths.Recovery);
            state = Journal.Read();
        }

        var installation = await db.InstallationState.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNowMilliseconds;
        try
        {
            Journal.Append(state, installation.InstallationId, seriesKey, provenHighWater, Guid.Empty, now);
        }
        catch (IOException)
        {
            return Result.Fail(ResultStatus.StorageUnavailable, "The number journal could not be written.");
        }

        series.LastReservedSequence = provenHighWater;
        series.Status = NumberSeriesStatus.Ready;
        series.LastReconciledUtc = now;
        series.EvidenceRef = evidence;
        series.Version++;
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Numbering.SeriesHoldResolved", "NumberSeries", seriesKey, evidence,
            new { provenHighWater });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }

    private static string BusinessDateOf(string seriesKey)
    {
        var digits = seriesKey[(Prefix.Length + 1)..];
        return DateOnly.ParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
