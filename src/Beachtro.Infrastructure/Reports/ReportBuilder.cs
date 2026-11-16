using System.Globalization;
using Beachtro.Application.Reports;
using Beachtro.Application.Shifts;
using Beachtro.Domain.Calendar;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Model;
using static Beachtro.Infrastructure.Reports.ReportValues;

namespace Beachtro.Infrastructure.Reports;

internal sealed record InventoryRow(string Sku, string Name, string Category, bool TrackStock, long? OnHandQ, bool IsActive);

internal sealed record ShiftRow(string CashierName, long OpenedUtc, long? ClosedUtc, ShiftCashSummary Cash, long? CountedM, long? VarianceM);

/// <summary>Context shared by every report: range, currency, calendar and test marking.</summary>
internal sealed record ReportContext(
    ReportType Type,
    string StartDate,
    string EndExclusiveDate,
    string CurrencyCode,
    int CurrencyExponent,
    CalendarPolicy Calendar,
    bool IsTest);

/// <summary>
/// Builds report documents from ledger facts using the reporting dictionary. Corrections reduce
/// the period of their own business date; original sales stay in their period and count.
/// </summary>
internal static class ReportBuilder
{
    public static readonly IReadOnlyDictionary<ReportType, string> Titles = new Dictionary<ReportType, string>
    {
        [ReportType.DailySales] = "Daily Sales",
        [ReportType.PaymentSummary] = "Payment Summary",
        [ReportType.ProductSales] = "Product Sales",
        [ReportType.InventoryStatus] = "Inventory Status",
        [ReportType.CashierShift] = "Cashier Shifts",
        [ReportType.MonthlySales] = "Monthly Sales",
        [ReportType.TransactionDetail] = "Transaction Detail",
    };

    private const string LedgerNote = "Amounts exclude tax unless labelled. Refunds and voids count on the date they were made; the original sale stays in its own period.";
    private const string TestNote = "TEST PROFILE: synthetic data with provisional tax settings. Not for tax filing.";

    public static ReportDocument Sales(ReportContext ctx, LedgerFacts facts, Func<string, string> periodOf, string periodHeader)
    {
        var periods = facts.Sales.Select(s => periodOf(s.BusinessDate)).Concat(facts.Corrections.Select(c => periodOf(c.BusinessDate)))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        var rows = periods.Select(p =>
        {
            var t = LedgerTotals.From(
                facts.Sales.Where(s => periodOf(s.BusinessDate) == p).ToArray(),
                facts.Corrections.Where(c => periodOf(c.BusinessDate) == p).ToArray());
            return PeriodRow(p, t);
        }).ToArray();
        var all = LedgerTotals.From(facts.Sales, facts.Corrections);

        var table = new ReportTable(
            $"By {periodHeader.ToLowerInvariant()}",
            [
                new(periodHeader, ReportCellKind.Text, 10),
                new("Sales", ReportCellKind.Count, 6),
                new("Gross ex-tax", ReportCellKind.Money, 14),
                new("Discounts", ReportCellKind.Money, 12),
                new("Net sales", ReportCellKind.Money, 14),
                new("Refunds", ReportCellKind.Count, 7),
                new("Refunded ex-tax", ReportCellKind.Money, 15),
                new("Net revenue", ReportCellKind.Money, 14),
                new("Net tax", ReportCellKind.Money, 12),
                new("Net collected", ReportCellKind.Money, 14),
            ],
            rows,
            PeriodRow("Total", all));

        return Document(ctx, [LedgerNote], Metrics(all), [table]);
    }

    public static ReportDocument PaymentSummary(ReportContext ctx, LedgerFacts facts)
    {
        var methods = new[] { PaymentMethod.Cash, PaymentMethod.GCash };
        var rows = methods.Select(m =>
        {
            var sales = facts.Sales.Where(s => s.Method == m).ToArray();
            var corrections = facts.Corrections.Where(c => c.Method == m).ToArray();
            var collected = ExactMath.CheckedSum(sales.Select(s => s.TotalM));
            var refunded = ExactMath.CheckedSum(corrections.Select(c => c.TotalM));
            return (IReadOnlyList<string>)[m.ToString(), Int(sales.Length), Int(collected), Int(corrections.Length), Int(refunded), Int(checked(collected - refunded))];
        }).ToArray();
        var all = LedgerTotals.From(facts.Sales, facts.Corrections);

        var table = new ReportTable(
            "By payment method (amounts include tax)",
            [
                new("Method", ReportCellKind.Text, 8),
                new("Sales", ReportCellKind.Count, 7),
                new("Collected", ReportCellKind.Money, 15),
                new("Refunds", ReportCellKind.Count, 7),
                new("Refunds paid", ReportCellKind.Money, 15),
                new("Net collected", ReportCellKind.Money, 15),
            ],
            rows,
            ["Total", Int(all.SaleCount), Int(all.SalesTotalM), Int(all.CorrectionCount), Int(all.CorrectionsTotalM), Int(all.NetCollectedM)]);

        return Document(ctx,
            ["Collected amounts are the sale totals applied to each method; cash handed back as change is not counted. This is the recorded amount, not a bank or GCash statement check."],
            [
                new("Net collected (incl. tax)", ReportCellKind.Money, Int(all.NetCollectedM)),
                new("Cash", ReportCellKind.Money, Int(all.NetCashM)),
                new("GCash", ReportCellKind.Money, Int(all.NetGCashM)),
            ],
            [table]);
    }

    public static ReportDocument ProductSales(ReportContext ctx, IReadOnlyList<ProductLineFact> lines)
    {
        var rows = lines.GroupBy(l => l.ProductId)
            .Select(g =>
            {
                // Name and SKU as most recently recorded in the period.
                var latest = g.OrderBy(l => l.EventUtc).Last();
                var sold = ExactMath.CheckedSum(g.Select(l => l.SoldQ));
                var returned = ExactMath.CheckedSum(g.Select(l => l.ReturnedQ));
                return (Sku: latest.Sku, Row: (IReadOnlyList<string>)
                [
                    latest.Sku, latest.Name, Int(sold), Int(returned), Int(checked(sold - returned)),
                    Int(ExactMath.CheckedSum(g.Select(l => l.NetM))), Int(ExactMath.CheckedSum(g.Select(l => l.TaxM))), Int(ExactMath.CheckedSum(g.Select(l => l.TotalM))),
                ]);
            })
            .OrderBy(r => r.Sku, StringComparer.Ordinal)
            .Select(r => r.Row)
            .ToArray();

        var table = new ReportTable(
            "By product",
            [
                new("SKU", ReportCellKind.Text, 16),
                new("Product", ReportCellKind.Text, 26),
                new("Sold", ReportCellKind.Quantity, 8),
                new("Returned", ReportCellKind.Quantity, 8),
                new("Net qty", ReportCellKind.Quantity, 8),
                new("Net sales ex-tax", ReportCellKind.Money, 16),
                new("Net tax", ReportCellKind.Money, 12),
                new("Net total", ReportCellKind.Money, 14),
            ],
            rows,
            ["Total", string.Empty, string.Empty, string.Empty, string.Empty,
                Int(ExactMath.CheckedSum(lines.Select(l => l.NetM))), Int(ExactMath.CheckedSum(lines.Select(l => l.TaxM))), Int(ExactMath.CheckedSum(lines.Select(l => l.TotalM)))]);

        return Document(ctx, ["Returned quantities are units refunded in this period, whether or not they went back to stock."], [], [table]);
    }

    public static ReportDocument InventoryStatus(ReportContext ctx, IReadOnlyList<InventoryRow> products)
    {
        var rows = products.OrderBy(p => p.Sku, StringComparer.Ordinal).Select(p => (IReadOnlyList<string>)
        [
            p.Sku, p.Name, p.Category, p.TrackStock ? "Yes" : "No", p.OnHandQ is { } q ? Int(q) : string.Empty, p.IsActive ? "Active" : "Inactive",
        ]).ToArray();
        var table = new ReportTable(
            "Stock on hand",
            [
                new("SKU", ReportCellKind.Text, 16),
                new("Product", ReportCellKind.Text, 28),
                new("Category", ReportCellKind.Text, 18),
                new("Tracked", ReportCellKind.Text, 7),
                new("On hand", ReportCellKind.Quantity, 10),
                new("Status", ReportCellKind.Text, 8),
            ],
            rows,
            null);

        var tracked = products.Where(p => p.TrackStock && p.IsActive).ToArray();
        return Document(ctx,
            ["Balances as of the generation time shown above. This is the current balance, not a reconstruction of a past date."],
            [
                new("Active tracked products", ReportCellKind.Count, Int(tracked.Length)),
                new("Out of stock (zero or less)", ReportCellKind.Count, Int(tracked.Count(p => p.OnHandQ <= 0))),
            ],
            [table]);
    }

    public static ReportDocument CashierShifts(ReportContext ctx, IReadOnlyList<ShiftRow> shifts)
    {
        var rows = shifts.OrderBy(s => s.OpenedUtc).Select(s => (IReadOnlyList<string>)
        [
            s.CashierName,
            ctx.Calendar.LocalTimeText(s.OpenedUtc),
            s.ClosedUtc is { } closed ? ctx.Calendar.LocalTimeText(closed) : "open",
            Int(s.Cash.OpeningMinor), Int(s.Cash.CashSalesMinor), Int(s.Cash.CashInMinor), Int(s.Cash.CashOutMinor), Int(s.Cash.CashRefundsMinor),
            Int(s.Cash.ExpectedMinor), s.CountedM is { } c ? Int(c) : string.Empty, s.VarianceM is { } v ? Int(v) : string.Empty,
        ]).ToArray();

        long Sum(Func<ShiftRow, long> f) => ExactMath.CheckedSum(shifts.Select(f));
        var closed = shifts.Where(s => s.ClosedUtc is not null).ToArray();
        var table = new ReportTable(
            "Shifts opened in the period",
            [
                new("Cashier", ReportCellKind.Text, 14),
                new("Opened", ReportCellKind.Text, 16),
                new("Closed", ReportCellKind.Text, 16),
                new("Opening", ReportCellKind.Money, 11),
                new("Cash sales", ReportCellKind.Money, 12),
                new("Cash in", ReportCellKind.Money, 10),
                new("Cash out", ReportCellKind.Money, 10),
                new("Refunds", ReportCellKind.Money, 10),
                new("Expected", ReportCellKind.Money, 12),
                new("Counted", ReportCellKind.Money, 12),
                new("Variance", ReportCellKind.Money, 10),
            ],
            rows,
            ["Total", string.Empty, string.Empty, Int(Sum(s => s.Cash.OpeningMinor)), Int(Sum(s => s.Cash.CashSalesMinor)), Int(Sum(s => s.Cash.CashInMinor)),
                Int(Sum(s => s.Cash.CashOutMinor)), Int(Sum(s => s.Cash.CashRefundsMinor)), Int(Sum(s => s.Cash.ExpectedMinor)),
                Int(ExactMath.CheckedSum(closed.Select(s => s.CountedM!.Value))), Int(ExactMath.CheckedSum(closed.Select(s => s.VarianceM!.Value)))]);

        return Document(ctx,
            [
                $"Times are {ctx.Calendar.TimeZoneId}. Expected = opening + cash sales + cash in - cash out - cash refunds; GCash is excluded.",
                "Closed shifts show the figures frozen at closing. Open shifts show live figures and no count.",
            ],
            [
                new("Shifts", ReportCellKind.Count, Int(shifts.Count)),
                new("Still open", ReportCellKind.Count, Int(shifts.Count - closed.Length)),
                new("Total variance (closed)", ReportCellKind.Money, Int(ExactMath.CheckedSum(closed.Select(s => s.VarianceM!.Value)))),
            ],
            [table]);
    }

    public static ReportDocument TransactionDetail(ReportContext ctx, LedgerFacts facts, IReadOnlyDictionary<Guid, string> names)
    {
        var events = facts.Sales.Select(s => (Utc: s.CompletedUtc, Key: s.Number, Row: (IReadOnlyList<string>)
            [
                s.BusinessDate, Time(ctx, s.CompletedUtc), s.Number, "Sale", string.Empty, names.GetValueOrDefault(s.CashierUserId, "?"), s.Method.ToString(),
                Int(s.NetM), Int(s.TaxM), Int(s.TotalM),
            ]))
            .Concat(facts.Corrections.Select(c => (Utc: c.CreatedUtc, Key: c.Number, Row: (IReadOnlyList<string>)
            [
                c.BusinessDate, Time(ctx, c.CreatedUtc), c.Number, c.Kind.ToString(), c.OriginalNumber, names.GetValueOrDefault(c.ProcessedByUserId, "?"), c.Method.ToString(),
                Int(-c.NetM), Int(-c.TaxM), Int(-c.TotalM),
            ])))
            .OrderBy(e => e.Utc).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Row)
            .ToArray();
        var all = LedgerTotals.From(facts.Sales, facts.Corrections);

        var table = new ReportTable(
            "Sales, refunds and voids",
            [
                new("Date", ReportCellKind.Text, 10),
                new("Time", ReportCellKind.Text, 5),
                new("Number", ReportCellKind.Text, 18),
                new("Type", ReportCellKind.Text, 6),
                new("Original sale", ReportCellKind.Text, 18),
                new("By", ReportCellKind.Text, 14),
                new("Paid", ReportCellKind.Text, 5),
                new("Net ex-tax", ReportCellKind.Money, 14),
                new("Tax", ReportCellKind.Money, 12),
                new("Total", ReportCellKind.Money, 14),
            ],
            events,
            ["Total", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Int(all.NetRevenueExTaxM), Int(all.NetTaxM), Int(all.NetCollectedM)]);

        return Document(ctx, [LedgerNote, $"Times are {ctx.Calendar.TimeZoneId}. Refunds and voids are negative."], Metrics(all), [table]);
    }

    public static IReadOnlyList<ReportMetric> Metrics(LedgerTotals t) =>
    [
        new("Sales (count)", ReportCellKind.Count, Int(t.SaleCount)),
        new("Gross sales ex-tax", ReportCellKind.Money, Int(t.GrossExTaxM)),
        new("Discounts ex-tax", ReportCellKind.Money, Int(t.DiscountExTaxM)),
        new("Net sales ex-tax", ReportCellKind.Money, Int(t.NetSalesExTaxM)),
        new("Refunds and voids (count)", ReportCellKind.Count, Int(t.CorrectionCount)),
        new("Refunded ex-tax", ReportCellKind.Money, Int(t.CorrectionNetExTaxM)),
        new("Net revenue ex-tax", ReportCellKind.Money, Int(t.NetRevenueExTaxM)),
        new("Net tax", ReportCellKind.Money, Int(t.NetTaxM)),
        new("Net collected (incl. tax)", ReportCellKind.Money, Int(t.NetCollectedM)),
        new("  Cash", ReportCellKind.Money, Int(t.NetCashM)),
        new("  GCash", ReportCellKind.Money, Int(t.NetGCashM)),
        new("Average sale (incl. tax)", ReportCellKind.Money, Int(t.AverageSaleM)),
    ];

    public static string MonthOf(string businessDate) => businessDate[..7];

    private static string Time(ReportContext ctx, long utc) => ctx.Calendar.LocalTimeText(utc)[11..];

    private static IReadOnlyList<string> PeriodRow(string label, LedgerTotals t) =>
    [
        label, Int(t.SaleCount), Int(t.GrossExTaxM), Int(t.DiscountExTaxM), Int(t.NetSalesExTaxM), Int(t.CorrectionCount),
        Int(t.CorrectionNetExTaxM), Int(t.NetRevenueExTaxM), Int(t.NetTaxM), Int(t.NetCollectedM),
    ];

    private static ReportDocument Document(ReportContext ctx, IReadOnlyList<string> notes, IReadOnlyList<ReportMetric> metrics, IReadOnlyList<ReportTable> tables) =>
        new(ctx.Type, Titles[ctx.Type], ctx.StartDate, ctx.EndExclusiveDate, ctx.CurrencyCode, ctx.CurrencyExponent, ctx.Calendar.TimeZoneId, ctx.IsTest,
            ctx.IsTest ? [TestNote, .. notes] : notes, metrics, tables);

    /// <summary>Inclusive last date for display.</summary>
    public static string LastDate(string endExclusiveDate) =>
        DateOnly.ParseExact(endExclusiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
