using Beachtro.Application.Common;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sales;

/// <summary>Scoped transaction history (FR-017). Read-only; never exposes tracked entities.</summary>
public sealed class OrderQueryService(IDbContextFactory<PosDbContext> contextFactory, IIdentityService identity) : IOrderQueryService
{
    public const int MaxTake = 500;

    public async Task<Result<IReadOnlyList<OrderSummary>>> ListAsync(UserSession session, string? businessDate, string? search, int take = 200, CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(session, cancellationToken).ConfigureAwait(false);
        if (scope.Failure is not null)
        {
            return scope.Failure.As<IReadOnlyList<OrderSummary>>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var orders = db.Orders.AsNoTracking();
        if (scope.OwnOnly)
        {
            orders = orders.Where(o => o.CashierUserId == session.UserId);
        }

        if (!string.IsNullOrWhiteSpace(businessDate))
        {
            orders = orders.Where(o => o.BusinessDate == businessDate);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            orders = orders.Where(o => o.TransactionNumber.Contains(term));
        }

        var page = await orders.OrderByDescending(o => o.CompletedUtc).ThenByDescending(o => o.Sequence)
            .Take(Math.Clamp(take, 1, MaxTake)).ToListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OrderSummary> summaries = await SummarizeAsync(db, page, cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<OrderSummary>>.Ok(summaries);
    }

    public async Task<Result<OrderDetail>> GetAsync(UserSession session, Guid orderId, CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(session, cancellationToken).ConfigureAwait(false);
        if (scope.Failure is not null)
        {
            return scope.Failure.As<OrderDetail>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);
        if (order is null || (scope.OwnOnly && order.CashierUserId != session.UserId))
        {
            return Result<OrderDetail>.Fail(ResultStatus.NotFound, "Sale not found.");
        }

        var summary = (await SummarizeAsync(db, [order], cancellationToken).ConfigureAwait(false))[0];
        var items = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == orderId).OrderBy(i => i.LineNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var itemIds = items.Select(i => i.Id).ToArray();
        var returned = await db.CorrectionItems.AsNoTracking().Where(c => itemIds.Contains(c.OriginalOrderItemId))
            .GroupBy(c => c.OriginalOrderItemId).Select(g => new { g.Key, Q = g.Sum(c => c.QuantityQ) })
            .ToDictionaryAsync(x => x.Key, x => x.Q, cancellationToken).ConfigureAwait(false);
        var corrections = (await db.Corrections.AsNoTracking().Where(c => c.OriginalOrderId == orderId).OrderBy(c => c.CreatedUtc)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(c => new CorrectionSummary(c.Id, c.CorrectionNumber, c.Kind, c.TotalM, c.CreatedUtc, c.Reason)).ToArray();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId, cancellationToken).ConfigureAwait(false);

        var lines = items.Select(i => new OrderLineDetail(i.Id, i.LineNo, i.SkuSnapshot, i.NameSnapshot, i.QuantityQ, i.UnitStepQ, i.UnitPriceM, i.TotalM,
            i.TrackStockSnapshot, returned.GetValueOrDefault(i.Id))).ToArray();
        return Result<OrderDetail>.Ok(new OrderDetail(summary, lines, corrections, payment.Reference, payment.TenderedM, payment.ChangeM));
    }

    private sealed record Scope(Result<Unit>? Failure, bool OwnOnly);

    private async Task<Scope> ScopeAsync(UserSession session, CancellationToken cancellationToken)
    {
        if ((await identity.AuthorizeAsync(session, Capabilities.OrdersViewAll, false, cancellationToken).ConfigureAwait(false)).IsSuccess)
        {
            return new Scope(null, false);
        }

        var own = await identity.AuthorizeAsync(session, Capabilities.OrdersViewOwn, false, cancellationToken).ConfigureAwait(false);
        return own.IsSuccess ? new Scope(null, true) : new Scope(own, true);
    }

    private static async Task<OrderSummary[]> SummarizeAsync(PosDbContext db, IReadOnlyList<Persistence.Entities.Order> orders, CancellationToken cancellationToken)
    {
        var ids = orders.Select(o => o.Id).ToArray();
        var cashierIds = orders.Select(o => o.CashierUserId).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(u => cashierIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        var methods = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.OrderId)).ToDictionaryAsync(p => p.OrderId, p => p.Method, cancellationToken).ConfigureAwait(false);
        var corrected = await db.Corrections.AsNoTracking().Where(c => ids.Contains(c.OriginalOrderId))
            .GroupBy(c => c.OriginalOrderId).Select(g => new { g.Key, Total = g.Sum(c => c.TotalM) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, cancellationToken).ConfigureAwait(false);

        return orders.Select(o => new OrderSummary(o.Id, o.TransactionNumber, o.BusinessDate, o.CompletedUtc, names.GetValueOrDefault(o.CashierUserId, "?"),
            o.OrderType, methods[o.Id], o.TotalM, corrected.GetValueOrDefault(o.Id), o.CurrencyExponent)).ToArray();
    }
}
