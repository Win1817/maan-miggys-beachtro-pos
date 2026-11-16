using Beachtro.Domain.Financial;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Reports;

internal sealed record SaleFact(
    Guid OrderId,
    string Number,
    string BusinessDate,
    long CompletedUtc,
    Guid CashierUserId,
    PaymentMethod Method,
    long NetM,
    long DiscountExTaxM,
    long TaxM,
    long TotalM,
    string CurrencyCode,
    int CurrencyExponent);

internal sealed record CorrectionFact(
    Guid CorrectionId,
    string Number,
    CorrectionKind Kind,
    string BusinessDate,
    long CreatedUtc,
    Guid ProcessedByUserId,
    string OriginalNumber,
    PaymentMethod Method,
    long NetM,
    long DiscountExTaxM,
    long TaxM,
    long TotalM);

/// <summary>One sold or returned product line, signed by event (returns are negative).</summary>
internal sealed record ProductLineFact(Guid ProductId, string Sku, string Name, long EventUtc, long SoldQ, long ReturnedQ, long NetM, long TaxM, long TotalM);

/// <summary>
/// Committed sale and correction events whose own business date is in [start, endExclusive)
/// (finance design §Reporting dictionary). Each event is read once; nothing is re-dated under a
/// newer calendar policy.
/// </summary>
internal sealed record LedgerFacts(IReadOnlyList<SaleFact> Sales, IReadOnlyList<CorrectionFact> Corrections)
{
    public long LastEventUtc => Math.Max(Sales.Count == 0 ? 0 : Sales.Max(s => s.CompletedUtc), Corrections.Count == 0 ? 0 : Corrections.Max(c => c.CreatedUtc));

    /// <summary>Stable identity of the included event set.</summary>
    public string InclusionSha256() =>
        CanonicalJson.Sha256(string.Join('\n', Sales.Select(s => "S:" + s.OrderId.ToString("D")).Concat(Corrections.Select(c => "C:" + c.CorrectionId.ToString("D"))).Order(StringComparer.Ordinal)));
}

/// <summary>The reporting dictionary computed from a set of facts. All sums are checked.</summary>
internal sealed record LedgerTotals(
    int SaleCount,
    long GrossExTaxM,
    long DiscountExTaxM,
    long NetSalesExTaxM,
    int CorrectionCount,
    long CorrectionNetExTaxM,
    long NetRevenueExTaxM,
    long NetTaxM,
    long NetCollectedM,
    long NetCashM,
    long NetGCashM,
    long SalesTotalM,
    long CorrectionsTotalM,
    long AverageSaleM)
{
    public static LedgerTotals From(IReadOnlyCollection<SaleFact> sales, IReadOnlyCollection<CorrectionFact> corrections)
    {
        var net = ExactMath.CheckedSum(sales.Select(s => s.NetM));
        var discount = ExactMath.CheckedSum(sales.Select(s => s.DiscountExTaxM));
        var tax = ExactMath.CheckedSum(sales.Select(s => s.TaxM));
        var total = ExactMath.CheckedSum(sales.Select(s => s.TotalM));
        var corrNet = ExactMath.CheckedSum(corrections.Select(c => c.NetM));
        var corrTax = ExactMath.CheckedSum(corrections.Select(c => c.TaxM));
        var corrTotal = ExactMath.CheckedSum(corrections.Select(c => c.TotalM));

        long Collected(PaymentMethod method) => checked(
            ExactMath.CheckedSum(sales.Where(s => s.Method == method).Select(s => s.TotalM))
            - ExactMath.CheckedSum(corrections.Where(c => c.Method == method).Select(c => c.TotalM)));

        return new LedgerTotals(
            sales.Count,
            checked(net + discount),
            discount,
            net,
            corrections.Count,
            corrNet,
            checked(net - corrNet),
            checked(tax - corrTax),
            checked(total - corrTotal),
            Collected(PaymentMethod.Cash),
            Collected(PaymentMethod.GCash),
            total,
            corrTotal,
            sales.Count == 0 ? 0 : ExactMath.RoundHalfAwayFromZero(total, sales.Count));
    }
}

internal static class LedgerQuery
{
    /// <summary>Safety bound for one report; a larger range must be split.</summary>
    public const int MaxEvents = 50_000;

    public static async Task<LedgerFacts> LoadAsync(PosDbContext db, string startDate, string endExclusiveDate, CancellationToken cancellationToken)
    {
        var sales = await (from o in db.Orders.AsNoTracking()
                           where string.Compare(o.BusinessDate, startDate) >= 0 && string.Compare(o.BusinessDate, endExclusiveDate) < 0
                           join p in db.Payments.AsNoTracking() on o.Id equals p.OrderId
                           select new SaleFact(o.Id, o.TransactionNumber, o.BusinessDate, o.CompletedUtc, o.CashierUserId, p.Method,
                               o.NetM, o.DiscountExTaxM, o.TaxM, o.TotalM, o.CurrencyCode, o.CurrencyExponent))
            .Take(MaxEvents + 1).ToListAsync(cancellationToken).ConfigureAwait(false);

        var corrections = await (from c in db.Corrections.AsNoTracking()
                                 where string.Compare(c.BusinessDate, startDate) >= 0 && string.Compare(c.BusinessDate, endExclusiveDate) < 0
                                 join o in db.Orders.AsNoTracking() on c.OriginalOrderId equals o.Id
                                 join r in db.RefundPayments.AsNoTracking() on c.Id equals r.CorrectionId
                                 select new CorrectionFact(c.Id, c.CorrectionNumber, c.Kind, c.BusinessDate, c.CreatedUtc, c.ApprovedByUserId,
                                     o.TransactionNumber, r.Method, c.NetM, c.DiscountExTaxM, c.TaxM, c.TotalM))
            .Take(MaxEvents + 1).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (sales.Count + corrections.Count > MaxEvents)
        {
            throw new ReportTooLargeException();
        }

        // Deterministic order: event time, then number.
        return new LedgerFacts(
            sales.OrderBy(s => s.CompletedUtc).ThenBy(s => s.Number, StringComparer.Ordinal).ToArray(),
            corrections.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Number, StringComparer.Ordinal).ToArray());
    }

    public static async Task<IReadOnlyList<ProductLineFact>> LoadProductLinesAsync(PosDbContext db, LedgerFacts facts, CancellationToken cancellationToken)
    {
        var orderIds = facts.Sales.Select(s => s.OrderId).ToArray();
        var completed = facts.Sales.ToDictionary(s => s.OrderId, s => s.CompletedUtc);
        var sold = (await db.OrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId))
                .Select(i => new { i.OrderId, i.ProductId, i.SkuSnapshot, i.NameSnapshot, i.QuantityQ, i.NetM, i.TaxM, i.TotalM })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(i => new ProductLineFact(i.ProductId, i.SkuSnapshot, i.NameSnapshot, completed[i.OrderId], i.QuantityQ, 0, i.NetM, i.TaxM, i.TotalM));

        var correctionIds = facts.Corrections.Select(c => c.CorrectionId).ToArray();
        var created = facts.Corrections.ToDictionary(c => c.CorrectionId, c => c.CreatedUtc);
        var returned = (await (from ci in db.CorrectionItems.AsNoTracking()
                               where correctionIds.Contains(ci.CorrectionId)
                               join oi in db.OrderItems.AsNoTracking() on ci.OriginalOrderItemId equals oi.Id
                               select new { ci.CorrectionId, oi.ProductId, oi.SkuSnapshot, oi.NameSnapshot, ci.QuantityQ, ci.NetM, ci.TaxM, ci.TotalM })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(i => new ProductLineFact(i.ProductId, i.SkuSnapshot, i.NameSnapshot, created[i.CorrectionId], 0, i.QuantityQ, -i.NetM, -i.TaxM, -i.TotalM));

        return sold.Concat(returned).ToArray();
    }
}

internal sealed class ReportTooLargeException() : Exception("Too many transactions for one report.");
