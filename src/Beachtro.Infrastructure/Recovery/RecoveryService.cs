using Beachtro.Application.Common;
using Beachtro.Application.Ports;
using Beachtro.Application.Recovery;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Recovery;

/// <summary>
/// The Recovery page's view of the restore runbook (IB-24, POS-DEC-010): what still blocks reopening
/// (number series on hold, unfinished payments), held uploads, the latest recovery run, and closing an
/// unfinished payment with the reason that records what really happened.
/// </summary>
public sealed class RecoveryService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IClock clock,
    WriteGate writeGate) : IRecoveryService
{
    public async Task<Result<RecoveryStatus>> GetStatusAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupRestore, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<RecoveryStatus>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var mode = await db.InstallationState.AsNoTracking().Select(s => s.Mode).SingleAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.RecoveryRuns.AsNoTracking().OrderByDescending(r => r.StartedUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var blocked = await db.NumberSeries.AsNoTracking().Where(s => s.Status == NumberSeriesStatus.Blocked).OrderBy(s => s.SeriesKey)
            .Select(s => new SeriesHold(s.SeriesKey, s.LastReservedSequence, s.EvidenceRef ?? "Blocked"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);

        var prepared = new List<PreparedRequestView>();
        foreach (var r in await db.CheckoutRequests.AsNoTracking().Where(r => r.State == RequestState.Prepared).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            long? total = null;
            try
            {
                total = SalePricing.ReadRequestJson(r.QuoteJson).TotalMinor;
            }
            catch (System.Text.Json.JsonException)
            {
                // Shown without a total; the request itself is still closed by reason.
            }

            prepared.Add(new PreparedRequestView(r.Id, false, r.PreparedUtc, names.GetValueOrDefault(r.ActorUserId, "unknown"), total, r.ExternalEvidenceJson is not null));
        }

        foreach (var r in await db.CorrectionRequests.AsNoTracking().Where(r => r.State == RequestState.Prepared).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            prepared.Add(new PreparedRequestView(r.Id, true, r.PreparedUtc, names.GetValueOrDefault(r.ActorUserId, "unknown"), null, r.PaymentEvidenceJson is not null));
        }

        var held = await db.SyncQueue.CountAsync(q => q.State == SyncState.RecoveryHold, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        if (blocked.Count > 0)
        {
            blockers.Add($"{blocked.Count} number series on hold: enter the highest number known to be used for each.");
        }

        if (prepared.Count > 0)
        {
            blockers.Add($"{prepared.Count} unfinished payment(s): close each with what really happened.");
        }

        return Result<RecoveryStatus>.Ok(new RecoveryStatus(
            mode,
            run is null ? null : new RecoveryRunView(run.Id, run.StartedUtc, run.FinishedUtc, run.ReconciledLastSale, run.PreservedDbPath, run.Disposition.ToString(), run.Notes),
            blocked,
            prepared.OrderBy(p => p.PreparedUtc).ToArray(),
            held,
            mode == InstallationMode.Normal ? [] : blockers));
    }

    public async Task<Result<Unit>> ClosePreparedRequestAsync(UserSession session, Guid requestId, string reason, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.BackupRestore, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var invalid = TextRules.ValidateRequired(reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var mode = await db.InstallationState.AsNoTracking().Select(s => s.Mode).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (mode != InstallationMode.RecoveryRequired)
        {
            return Result.Fail(ResultStatus.Conflict, "Unfinished payments are closed here only during recovery. Otherwise the cashier resolves them at the till.");
        }

        var now = clock.UtcNowMilliseconds;
        var checkout = await db.CheckoutRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        var correction = checkout is null ? await db.CorrectionRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false) : null;
        if (checkout is null && correction is null)
        {
            return Result.Fail(ResultStatus.NotFound, "That request was not found.");
        }

        if ((checkout?.State ?? correction!.State) != RequestState.Prepared)
        {
            return Result.Fail(ResultStatus.Conflict, "That request is already resolved.");
        }

        if (checkout is not null)
        {
            checkout.State = RequestState.Abandoned;
            checkout.ResolvedUtc = now;
            checkout.Version++;
        }
        else
        {
            correction!.State = RequestState.Abandoned;
            correction.ResolvedUtc = now;
            correction.Version++;
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Recovery.PreparedRequestClosed", checkout is not null ? "CheckoutRequest" : "CorrectionRequest",
            requestId.ToString(), reason, new { kind = checkout is not null ? "sale" : "refund" }, requestId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Ok();
    }
}
