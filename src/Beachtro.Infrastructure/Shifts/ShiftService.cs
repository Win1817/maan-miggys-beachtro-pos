using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Security;
using Beachtro.Application.Shifts;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Shifts;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Sales;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Shifts;

/// <summary>
/// Cashier shifts and cash movements (IB-16; FR-019, FR-020). Expected cash is always computed
/// from committed sale payments, refund payments and movements linked to the shift, never typed
/// in. Closing writes counted cash, variance and a canonical snapshot of the equation inputs in
/// one update; the closed row is then protected by a guard trigger.
/// </summary>
public sealed class ShiftService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IClock clock,
    WriteGate writeGate) : IShiftService
{
    public const string CloseSnapshotSchema = "shift-close.v1";
    public const int MaxReasonLength = 500;

    public async Task<Result<ShiftDetail?>> GetCurrentAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ShiftsOwn, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<ShiftDetail?>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await OpenShiftOfAsync(db, session.UserId, cancellationToken).ConfigureAwait(false);
        return Result<ShiftDetail?>.Ok(shift is null ? null : await DetailAsync(db, shift, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<ShiftDetail>> OpenAsync(UserSession session, long openingCashMinor, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.ShiftsOwn, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<ShiftDetail>();
        }

        if (openingCashMinor is < 0 or > FinancialLimits.MaxDocumentMinor)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Validation, "Opening cash must be zero or more.");
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var blocked = await CheckoutService.RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<ShiftDetail>();
        }

        if (await OpenShiftOfAsync(db, session.UserId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Conflict, "You already have an open shift. Close it before opening another.");
        }

        var now = clock.UtcNowMilliseconds;
        var shift = new CashierShift
        {
            Id = Guid.NewGuid(),
            CashierUserId = session.UserId,
            OpenedUtc = now,
            OpeningCashM = openingCashMinor,
        };
        db.CashierShifts.Add(shift);
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Shifts.Opened", "CashierShift", shift.Id.ToString(), null,
            new { openingCashMinor });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<ShiftDetail>.Ok(await DetailAsync(db, shift, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<CashMovementView>> RecordMovementAsync(
        UserSession session,
        Guid requestId,
        Guid shiftId,
        CashDirection direction,
        long amountMinor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.CashMovements, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<CashMovementView>();
        }

        if (amountMinor is <= 0 or > FinancialLimits.MaxDocumentMinor)
        {
            return Result<CashMovementView>.Fail(ResultStatus.Validation, "Enter an amount greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
        {
            return Result<CashMovementView>.Fail(ResultStatus.Validation, "A reason is required for every cash in or out.");
        }

        if (direction is not (CashDirection.In or CashDirection.Out))
        {
            return Result<CashMovementView>.Fail(ResultStatus.Validation, "Choose cash in or cash out.");
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent repeat: the same request returns the recorded movement and writes nothing.
        var existing = await db.CashMovements.AsNoTracking().SingleOrDefaultAsync(m => m.RequestId == requestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.ShiftId == shiftId && existing.Direction == direction && existing.AmountM == amountMinor
                ? Result<CashMovementView>.Ok(await MovementViewAsync(db, existing, cancellationToken).ConfigureAwait(false))
                : Result<CashMovementView>.Fail(ResultStatus.Conflict, "This request was already used for a different cash movement.");
        }

        var blocked = await CheckoutService.RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<CashMovementView>();
        }

        var shift = await db.CashierShifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shiftId, cancellationToken).ConfigureAwait(false);
        if (shift is null)
        {
            return Result<CashMovementView>.Fail(ResultStatus.NotFound, "Shift not found.");
        }

        if (shift.ClosedUtc is not null)
        {
            return Result<CashMovementView>.Fail(ResultStatus.Conflict, "This shift is closed. Cash movements need an open shift.");
        }

        if (direction == CashDirection.Out)
        {
            var summary = await SummarizeAsync(db, shift, cancellationToken).ConfigureAwait(false);
            if (amountMinor > summary.ExpectedMinor)
            {
                return Result<CashMovementView>.Fail(ResultStatus.Validation, "Cash out is more than the drawer is expected to hold.");
            }
        }

        var now = clock.UtcNowMilliseconds;
        var movement = new CashMovement
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            Direction = direction,
            AmountM = amountMinor,
            Reason = reason.Trim(),
            ActorUserId = session.UserId,
            RequestId = requestId,
            CreatedUtc = now,
        };
        db.CashMovements.Add(movement);
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Shifts.Cash" + direction, "CashMovement", movement.Id.ToString(), movement.Reason,
            new { shiftId, amountMinor }, requestId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<CashMovementView>.Ok(new CashMovementView(movement.Id, direction, amountMinor, movement.Reason, session.DisplayName, now));
    }

    public async Task<Result<ShiftDetail>> CloseAsync(UserSession session, Guid shiftId, long countedCashMinor, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (countedCashMinor is < 0 or > FinancialLimits.MaxDocumentMinor)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Validation, "Counted cash must be zero or more.");
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > MaxReasonLength })
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Validation, "The reason is too long.");
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await db.CashierShifts.SingleOrDefaultAsync(s => s.Id == shiftId, cancellationToken).ConfigureAwait(false);

        var own = shift is not null && shift.CashierUserId == session.UserId;
        var auth = own
            ? await identity.AuthorizeAsync(session, Capabilities.ShiftsOwn, false, cancellationToken).ConfigureAwait(false)
            : await identity.AuthorizeAsync(session, Capabilities.ShiftsManage, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<ShiftDetail>();
        }

        if (shift is null)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.NotFound, "Shift not found.");
        }

        if (shift.ClosedUtc is not null)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Conflict, "This shift is already closed.");
        }

        if (!own && trimmed is null)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Validation, "Closing another cashier's shift needs a reason.");
        }

        var blocked = await CheckoutService.RequireNormalModeAsync(db, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked.As<ShiftDetail>();
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var summary = await SummarizeAsync(db, shift, cancellationToken).ConfigureAwait(false);
        var variance = ShiftCashCalculator.Variance(countedCashMinor, summary.ExpectedMinor);
        if (variance != 0 && trimmed is null)
        {
            return Result<ShiftDetail>.Fail(ResultStatus.Validation,
                $"The count is {(variance > 0 ? "over" : "short")} by {Receipts.ReceiptRenderer.Money(Math.Abs(variance), Currency.Php.Exponent)}. Enter a reason to close.");
        }

        var now = clock.UtcNowMilliseconds;
        var movementCount = await db.CashMovements.CountAsync(m => m.ShiftId == shift.Id, cancellationToken).ConfigureAwait(false);
        shift.ClosedUtc = now;
        shift.CountedCashM = countedCashMinor;
        shift.ExpectedCashM = summary.ExpectedMinor;
        shift.VarianceM = variance;
        shift.ClosedByUserId = session.UserId;
        shift.CloseReason = trimmed;
        shift.CloseSnapshotJson = CloseSnapshot(shift, summary, movementCount);
        shift.CloseSnapshotSha256 = CanonicalJson.Sha256(shift.CloseSnapshotJson);
        shift.Version++;

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, own ? "Shifts.Closed" : "Shifts.ClosedByManager", "CashierShift", shift.Id.ToString(), trimmed,
            new { expected = summary.ExpectedMinor, counted = countedCashMinor, variance });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<ShiftDetail>.Ok(await DetailAsync(db, shift, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<ShiftDetail>> GetAsync(UserSession session, Guid shiftId, CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(session, cancellationToken).ConfigureAwait(false);
        if (scope.Failure is not null)
        {
            return scope.Failure.As<ShiftDetail>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await db.CashierShifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shiftId, cancellationToken).ConfigureAwait(false);
        if (shift is null || (scope.OwnOnly && shift.CashierUserId != session.UserId))
        {
            return Result<ShiftDetail>.Fail(ResultStatus.NotFound, "Shift not found.");
        }

        return Result<ShiftDetail>.Ok(await DetailAsync(db, shift, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<IReadOnlyList<ShiftView>>> ListAsync(UserSession session, bool openOnly, int take = 100, CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(session, cancellationToken).ConfigureAwait(false);
        if (scope.Failure is not null)
        {
            return scope.Failure.As<IReadOnlyList<ShiftView>>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.CashierShifts.AsNoTracking();
        if (scope.OwnOnly)
        {
            query = query.Where(s => s.CashierUserId == session.UserId);
        }

        if (openOnly)
        {
            query = query.Where(s => s.ClosedUtc == null);
        }

        var shifts = await query.OrderByDescending(s => s.OpenedUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var views = new List<ShiftView>(shifts.Count);
        foreach (var shift in shifts)
        {
            views.Add(await ViewAsync(db, shift, cancellationToken).ConfigureAwait(false));
        }

        return Result<IReadOnlyList<ShiftView>>.Ok(views);
    }

    public async Task<bool> IsShiftRequiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await IsShiftRequiredAsync(db, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- shared with checkout and reports

    internal static async Task<bool> IsShiftRequiredAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var json = await db.BusinessSettings.AsNoTracking().Where(s => s.Key == OperationalSettingKeys.ShiftsRequired).Select(s => s.ValueJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(json?.Trim(), "true", StringComparison.Ordinal);
    }

    /// <summary>Validation failure when the setting requires a shift and the cashier has none open.</summary>
    internal static async Task<Result<Unit>?> RequireShiftIfConfiguredAsync(PosDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsShiftRequiredAsync(db, cancellationToken).ConfigureAwait(false)
            || await db.CashierShifts.AnyAsync(s => s.CashierUserId == userId && s.ClosedUtc == null, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Result.Fail(ResultStatus.Validation, "Open your shift before selling (Shifts page).");
    }

    internal static async Task<ShiftCashSummary> SummarizeAsync(PosDbContext db, CashierShift shift, CancellationToken cancellationToken)
    {
        var sales = await (from o in db.Orders.AsNoTracking()
                           where o.ShiftId == shift.Id
                           join p in db.Payments.AsNoTracking() on o.Id equals p.OrderId
                           select new { p.Method, p.AppliedM }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var refunds = await (from c in db.Corrections.AsNoTracking()
                             where c.ShiftId == shift.Id
                             join r in db.RefundPayments.AsNoTracking() on c.Id equals r.CorrectionId
                             select new { r.Method, r.AmountM }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var movements = await db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id)
            .Select(m => new { m.Direction, m.AmountM }).ToListAsync(cancellationToken).ConfigureAwait(false);

        var cashSales = ExactMath.CheckedSum(sales.Where(s => s.Method == PaymentMethod.Cash).Select(s => s.AppliedM));
        var cashRefunds = ExactMath.CheckedSum(refunds.Where(r => r.Method == PaymentMethod.Cash).Select(r => r.AmountM));
        var cashIn = ExactMath.CheckedSum(movements.Where(m => m.Direction == CashDirection.In).Select(m => m.AmountM));
        var cashOut = ExactMath.CheckedSum(movements.Where(m => m.Direction == CashDirection.Out).Select(m => m.AmountM));
        return new ShiftCashSummary(
            shift.OpeningCashM,
            cashSales,
            cashIn,
            cashOut,
            cashRefunds,
            ShiftCashCalculator.Expected(shift.OpeningCashM, cashSales, cashIn, cashOut, cashRefunds),
            sales.Count,
            refunds.Count,
            ExactMath.CheckedSum(sales.Where(s => s.Method == PaymentMethod.GCash).Select(s => s.AppliedM)),
            ExactMath.CheckedSum(refunds.Where(r => r.Method == PaymentMethod.GCash).Select(r => r.AmountM)));
    }

    /// <summary>
    /// A closed shift reports the figures frozen at close; refunds paid in a later shift never
    /// change it. An open shift reports live figures.
    /// </summary>
    internal static async Task<ShiftView> ViewAsync(PosDbContext db, CashierShift shift, CancellationToken cancellationToken)
    {
        var names = await NamesAsync(db, [shift.CashierUserId, shift.ClosedByUserId ?? Guid.Empty], cancellationToken).ConfigureAwait(false);
        var cash = shift.CloseSnapshotJson is { } json ? ReadSnapshot(json) : await SummarizeAsync(db, shift, cancellationToken).ConfigureAwait(false);
        return new ShiftView(
            shift.Id,
            shift.CashierUserId,
            names.GetValueOrDefault(shift.CashierUserId, "?"),
            shift.OpenedUtc,
            shift.ClosedUtc,
            cash,
            shift.CountedCashM,
            shift.VarianceM,
            shift.ClosedByUserId is { } closer ? names.GetValueOrDefault(closer, "?") : null,
            shift.CloseReason);
    }

    internal static string CloseSnapshot(CashierShift shift, ShiftCashSummary s, int movementCount) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", CloseSnapshotSchema);
            w.WriteString("shiftId", shift.Id.ToString("D"));
            w.WriteString("cashierUserId", shift.CashierUserId.ToString("D"));
            w.WriteNumber("openedUtc", shift.OpenedUtc);
            w.WriteNumber("closedUtc", shift.ClosedUtc!.Value);
            w.WriteNumber("openingMinor", s.OpeningMinor);
            w.WriteNumber("cashSalesMinor", s.CashSalesMinor);
            w.WriteNumber("cashInMinor", s.CashInMinor);
            w.WriteNumber("cashOutMinor", s.CashOutMinor);
            w.WriteNumber("cashRefundsMinor", s.CashRefundsMinor);
            w.WriteNumber("expectedMinor", s.ExpectedMinor);
            w.WriteNumber("countedMinor", shift.CountedCashM!.Value);
            w.WriteNumber("varianceMinor", shift.VarianceM!.Value);
            w.WriteNumber("saleCount", s.SaleCount);
            w.WriteNumber("refundCount", s.RefundCount);
            w.WriteNumber("movementCount", movementCount);
            w.WriteNumber("gcashSalesMinor", s.GCashSalesMinor);
            w.WriteNumber("gcashRefundsMinor", s.GCashRefundsMinor);
            w.WriteString("closedByUserId", shift.ClosedByUserId!.Value.ToString("D"));
            if (shift.CloseReason is { } reason)
            {
                w.WriteString("reason", reason);
            }
            else
            {
                w.WriteNull("reason");
            }

            w.WriteEndObject();
        });

    internal static ShiftCashSummary ReadSnapshot(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.GetProperty("schema").GetString() != CloseSnapshotSchema)
        {
            throw new FormatException("Unsupported shift close snapshot.");
        }

        return new ShiftCashSummary(
            r.GetProperty("openingMinor").GetInt64(),
            r.GetProperty("cashSalesMinor").GetInt64(),
            r.GetProperty("cashInMinor").GetInt64(),
            r.GetProperty("cashOutMinor").GetInt64(),
            r.GetProperty("cashRefundsMinor").GetInt64(),
            r.GetProperty("expectedMinor").GetInt64(),
            r.GetProperty("saleCount").GetInt32(),
            r.GetProperty("refundCount").GetInt32(),
            r.GetProperty("gcashSalesMinor").GetInt64(),
            r.GetProperty("gcashRefundsMinor").GetInt64());
    }

    internal static async Task<Dictionary<Guid, string>> NamesAsync(PosDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    private static Task<CashierShift?> OpenShiftOfAsync(PosDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.CashierShifts.AsNoTracking().SingleOrDefaultAsync(s => s.CashierUserId == userId && s.ClosedUtc == null, cancellationToken);

    private static async Task<ShiftDetail> DetailAsync(PosDbContext db, CashierShift shift, CancellationToken cancellationToken)
    {
        var view = await ViewAsync(db, shift, cancellationToken).ConfigureAwait(false);
        var movements = await db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).OrderBy(m => m.CreatedUtc).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(db, movements.Select(m => m.ActorUserId), cancellationToken).ConfigureAwait(false);
        return new ShiftDetail(view, movements.Select(m => new CashMovementView(m.Id, m.Direction, m.AmountM, m.Reason, names.GetValueOrDefault(m.ActorUserId, "?"), m.CreatedUtc)).ToArray());
    }

    private static async Task<CashMovementView> MovementViewAsync(PosDbContext db, CashMovement m, CancellationToken cancellationToken)
    {
        var names = await NamesAsync(db, [m.ActorUserId], cancellationToken).ConfigureAwait(false);
        return new CashMovementView(m.Id, m.Direction, m.AmountM, m.Reason, names.GetValueOrDefault(m.ActorUserId, "?"), m.CreatedUtc);
    }

    private sealed record Scope(Result<Unit>? Failure, bool OwnOnly);

    private async Task<Scope> ScopeAsync(UserSession session, CancellationToken cancellationToken)
    {
        if ((await identity.AuthorizeAsync(session, Capabilities.ShiftsManage, false, cancellationToken).ConfigureAwait(false)).IsSuccess)
        {
            return new Scope(null, false);
        }

        var own = await identity.AuthorizeAsync(session, Capabilities.ShiftsOwn, false, cancellationToken).ConfigureAwait(false);
        return own.IsSuccess ? new Scope(null, true) : new Scope(own, true);
    }
}
