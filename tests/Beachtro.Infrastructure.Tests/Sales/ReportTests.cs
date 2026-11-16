using System.Text;
using Beachtro.Application.Common;
using Beachtro.Application.Reports;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Reports;
using Microsoft.Data.Sqlite;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>T-12 reports and exports (FR-021 to FR-023). Test clock: 2025-11-16 21:33 Manila.</summary>
public sealed class ReportTests : IAsyncLifetime
{
    private const string Day1 = "2025-11-16";
    private const string Day2 = "2025-11-17";
    private const string Day3 = "2025-11-18";

    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private Guid ItemOf(SaleReceipt sale) =>
        Guid.Parse(_h.Security.Database.Scalar<string>($"SELECT Id FROM OrderItems WHERE OrderId = '{sale.OrderId}' AND LineNo = 1"));

    private async Task RefundOneAsync(SaleReceipt sale, RefundEvidence evidence)
    {
        await _h.ReauthOwnerAsync();
        var requestId = Guid.NewGuid();
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, requestId,
            new CorrectionInput(sale.OrderId, CorrectionKind.Refund, [new CorrectionLineInput(ItemOf(sale), 1000, StockDisposition.Return)], "customer returned item")));
        Ok(await _h.Corrections.CompleteAsync(_h.Owner, requestId, evidence));
    }

    private async Task<ReportRunView> RunAsync(ReportType type, string start, string endExclusive) =>
        Ok(await _h.Reports.GenerateAsync(_h.Owner, new ReportRequest(type, start, endExclusive)));

    private static string Metric(ReportRunView run, string label) => run.Document.Metrics.Single(m => m.Label == label).Value;

    private static string Csv(ReportFile file) => Encoding.UTF8.GetString(file.Content.AsSpan(3));

    /// <summary>Day 1: two cash teas (200), one GCash taxed item (35). Day 2: one tea refunded in cash.</summary>
    private async Task<SaleReceipt> SeedTwoDaysAsync()
    {
        var teas = await _h.SellCashAsync((_h.Tea, 2));
        await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Taxed, 1)), new GCashPayment(true, "GC-1", null));
        _h.Clock.Advance(TimeSpan.FromHours(3));
        await RefundOneAsync(teas, new CashPaidOut());
        return teas;
    }

    [Fact]
    public async Task Refund_reduces_its_own_day_and_the_range_reconciles()
    {
        await SeedTwoDaysAsync();

        var day1 = await RunAsync(ReportType.DailySales, Day1, Day2);
        Assert.Equal("2", Metric(day1, "Sales (count)"));
        Assert.Equal("0", Metric(day1, "Refunds and voids (count)"));
        Assert.Equal("23500", Metric(day1, "Net collected (incl. tax)"));
        Assert.Equal("11750", Metric(day1, "Average sale (incl. tax)"));

        var day2 = await RunAsync(ReportType.DailySales, Day2, Day3);
        Assert.Equal("0", Metric(day2, "Sales (count)"));
        Assert.Equal("10000", Metric(day2, "Refunded ex-tax"));
        Assert.Equal("-10000", Metric(day2, "Net revenue ex-tax"));
        Assert.Equal("-10000", Metric(day2, "  Cash"));

        var range = await RunAsync(ReportType.DailySales, Day1, Day3);
        var table = range.Document.Tables.Single();
        Assert.Equal([Day1, Day2], table.Rows.Select(r => r[0]));
        Assert.Equal("13500", Metric(range, "Net collected (incl. tax)"));
        Assert.Equal("10000", Metric(range, "  Cash"));
        Assert.Equal("3500", Metric(range, "  GCash"));

        // Net revenue = net sales - refunded; tax nets out the same way (35.00 incl. 12% = 31.25 + 3.75).
        Assert.Equal(long.Parse(Metric(range, "Net sales ex-tax")) - long.Parse(Metric(range, "Refunded ex-tax")), long.Parse(Metric(range, "Net revenue ex-tax")));
        Assert.Equal("375", Metric(range, "Net tax"));
        Assert.Equal(table.Totals![9], Metric(range, "Net collected (incl. tax)"));
    }

    [Fact]
    public async Task Monthly_payment_product_and_transaction_reports_agree()
    {
        await SeedTwoDaysAsync();

        var monthly = await RunAsync(ReportType.MonthlySales, "2025-11-01", "2025-12-01");
        Assert.Equal(["2025-11"], monthly.Document.Tables.Single().Rows.Select(r => r[0]));
        Assert.Equal("13500", Metric(monthly, "Net collected (incl. tax)"));

        var payments = await RunAsync(ReportType.PaymentSummary, Day1, Day3);
        var cash = payments.Document.Tables.Single().Rows.Single(r => r[0] == "Cash");
        Assert.Equal(["Cash", "1", "20000", "1", "10000", "10000"], cash);
        Assert.Equal("13500", payments.Document.Tables.Single().Totals![5]);

        var products = await RunAsync(ReportType.ProductSales, Day1, Day3);
        var tea = products.Document.Tables.Single().Rows.Single(r => r[0] == "TEST-TEA");
        Assert.Equal(["TEST-TEA", "Synthetic Iced Tea", "2000", "1000", "1000", "10000", "0", "10000"], tea);

        var detail = await RunAsync(ReportType.TransactionDetail, Day1, Day3);
        var rows = detail.Document.Tables.Single().Rows;
        Assert.Equal(["Sale", "Sale", "Refund"], rows.Select(r => r[3]));
        Assert.Equal("MB-20251116-000001", rows[2][4]);
        Assert.Equal("-10000", rows[2][9]);
        Assert.Equal("21:33", rows[0][1]);
        Assert.Equal("13500", detail.Document.Tables.Single().Totals![9]);
    }

    [Fact]
    public async Task Empty_range_gives_zero_totals_not_an_error()
    {
        var run = await RunAsync(ReportType.DailySales, "2024-01-01", "2024-01-08");
        Assert.Equal("0", Metric(run, "Sales (count)"));
        Assert.Equal("0", Metric(run, "Net collected (incl. tax)"));
        Assert.Empty(run.Document.Tables.Single().Rows);
        Assert.True(run.Document.IsTest);

        var pdf = Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Pdf));
        Assert.Contains("no data in this period", Encoding.Latin1.GetString(pdf.Content), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unchanged_report_reuses_its_run_and_new_activity_makes_a_revision()
    {
        await _h.SellCashAsync((_h.Tea, 1));
        var first = await RunAsync(ReportType.DailySales, Day1, Day2);
        var again = await RunAsync(ReportType.DailySales, Day1, Day2);
        Assert.Equal(first.RunId, again.RunId);
        Assert.True(again.ReusedExistingRun);
        Assert.Equal(1, again.Revision);

        await _h.SellCashAsync((_h.Cookie, 1));
        var second = await RunAsync(ReportType.DailySales, Day1, Day2);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.WatermarkSha256, second.WatermarkSha256);

        // Revision 1 is unchanged and still renders its original figures.
        var old = Ok(await _h.Reports.GetRunAsync(_h.Owner, first.RunId));
        Assert.Equal("1", Metric(old, "Sales (count)"));
        Assert.Throws<SqliteException>(() => _h.Security.Database.Execute($"UPDATE ReportRuns SET Revision = 9 WHERE Id = '{first.RunId}'"));
    }

    [Fact]
    public async Task Csv_is_escaped_invariant_and_neutralizes_formulas()
    {
        var evil = await _h.AddProductAsync("=CMD", "=HYPERLINK(\"http://x\",\"pay\")", 12_50, "NONE", stockUnits: 5);
        var sale = await _h.SellCashAsync((evil, 2));
        await RefundOneAsync(sale, new CashPaidOut());

        var run = await RunAsync(ReportType.ProductSales, Day1, Day2);
        var csv = Csv(Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Csv)));
        var file = Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Csv));

        Assert.Equal(Encoding.UTF8.GetPreamble(), file.Content[..3]);
        Assert.Contains("\"'=CMD\",\"'=HYPERLINK(\"\"http://x\"\",\"\"pay\"\")\",\"2\",\"1\",\"1\",\"12.50\",\"0.00\",\"12.50\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\"=", csv, StringComparison.Ordinal);
        Assert.Contains("\r\n", csv, StringComparison.Ordinal);

        var detail = await RunAsync(ReportType.TransactionDetail, Day1, Day2);
        var detailCsv = Csv(Ok(await _h.Reports.RenderAsync(_h.Owner, detail.RunId, ReportFormat.Csv)));
        Assert.Contains("\"-12.50\"", detailCsv, StringComparison.Ordinal); // negative amounts stay numeric
        Assert.DoesNotContain("'-", detailCsv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_and_csv_are_deterministic_and_show_the_same_totals()
    {
        await SeedTwoDaysAsync();
        var run = await RunAsync(ReportType.DailySales, Day1, Day3);

        var pdf1 = Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Pdf));
        var pdf2 = Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Pdf));
        var csv1 = Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Csv));
        var csv2 = Ok(await _h.Reports.RenderAsync(_h.Owner, run.RunId, ReportFormat.Csv));
        Assert.Equal(pdf1.Content, pdf2.Content);
        Assert.Equal(csv1.Content, csv2.Content);
        Assert.Equal($"daily-sales_{Day1}_to_{Day2}_r1_" + pdf1.FileName[^12..], pdf1.FileName);
        Assert.EndsWith(".pdf", pdf1.FileName, StringComparison.Ordinal);

        var pdfText = Encoding.Latin1.GetString(pdf1.Content);
        Assert.StartsWith("%PDF-1.4", pdfText, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", pdfText, StringComparison.Ordinal);
        Assert.Contains("135.00", pdfText, StringComparison.Ordinal);
        Assert.Contains("TEST - NOT FOR TAX FILING", pdfText, StringComparison.Ordinal);
        Assert.Contains("\"Net collected (incl. tax)\",\"135.00\"", Csv(csv1), StringComparison.Ordinal);

        // One artifact intent per run and format, however often it is exported.
        Assert.Equal(2L, _h.Count("ArtifactRecords", $"ReportRunId = '{run.RunId}' AND State = 'Intent'"));
        Assert.Equal(4L, _h.Count("AuditLogs", "Action = 'Reports.Exported'"));
    }

    [Fact]
    public async Task Business_close_records_a_watermark_and_shows_later_changes()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var close = Ok(await _h.Reports.CloseBusinessDayAsync(_h.Owner, Day1));
        Assert.Equal(1, close.Revision);
        Assert.False(Ok(await _h.Reports.GetBusinessCloseAsync(_h.Owner, Day1))!.ChangedSinceClose);
        Assert.Equal(ResultStatus.Validation, (await _h.Reports.CloseBusinessDayAsync(_h.Owner, Day2)).Status);

        // A refund the same evening changes the day after its close; nothing is locked.
        await RefundOneAsync(sale, new CashPaidOut());
        Assert.True(Ok(await _h.Reports.GetBusinessCloseAsync(_h.Owner, Day1))!.ChangedSinceClose);
        Assert.Contains("changed since", Ok(await _h.Reports.GetDashboardAsync(_h.Owner, Day1)).LastCloseSummary, StringComparison.Ordinal);

        Assert.Equal(2, Ok(await _h.Reports.CloseBusinessDayAsync(_h.Owner, Day1)).Revision);
        Assert.False(Ok(await _h.Reports.GetBusinessCloseAsync(_h.Owner, Day1))!.ChangedSinceClose);

        // A normal daily report of the same day is a separate revision chain.
        Assert.Equal(1, (await RunAsync(ReportType.DailySales, Day1, Day2)).Revision);
        Assert.Equal(2L, _h.Count("AuditLogs", "Action = 'Reports.BusinessDayClosed'"));
    }

    [Fact]
    public async Task Dashboard_shows_the_daily_dictionary_and_health()
    {
        var shift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 0)).Shift;
        await _h.SellCashAsync((_h.Tea, 10)); // sells out the tea
        await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Cookie, 1)), new GCashPayment(true, "GC-2", null));

        var d = Ok(await _h.Reports.GetDashboardAsync(_h.Owner, Day1));
        Assert.Equal(2, d.SaleCount);
        Assert.Equal(1_050_00, d.NetCollectedMinor);
        Assert.Equal(1_000_00, d.NetCashMinor);
        Assert.Equal(50_00, d.NetGCashMinor);
        Assert.Equal(525_00, d.AverageSaleMinor);
        Assert.Equal(1, d.OpenShiftCount);
        Assert.Equal(1, d.OutOfStockCount);
        Assert.Null(d.LastCloseSummary);
        Assert.Equal(ResultStatus.Validation, (await _h.Reports.GetDashboardAsync(_h.Owner, "16/11/2025")).Status);
        Ok(await _h.Shifts.CloseAsync(_h.Owner, shift.ShiftId, 1_000_00, null));
    }

    [Fact]
    public async Task Inventory_and_shift_reports_reflect_current_state()
    {
        var shift = Ok(await _h.Shifts.OpenAsync(_h.Owner, 100_00)).Shift;
        await _h.SellCashAsync((_h.Cookie, 3));
        Ok(await _h.Shifts.CloseAsync(_h.Owner, shift.ShiftId, 245_00, "short five"));

        var inventory = await RunAsync(ReportType.InventoryStatus, "2000-01-01", "2000-01-02");
        Assert.Equal(Day1, inventory.Document.StartDate); // always today's balance
        var cookie = inventory.Document.Tables.Single().Rows.Single(r => r[0] == "TEST-COOKIE");
        Assert.Equal("7000", cookie[4]);
        Assert.Equal(string.Empty, inventory.Document.Tables.Single().Rows.Single(r => r[0] == "TEST-SERVICE")[4]);
        Assert.Equal(inventory.RunId, (await RunAsync(ReportType.InventoryStatus, Day1, Day2)).RunId);

        var shifts = await RunAsync(ReportType.CashierShift, Day1, Day2);
        var row = shifts.Document.Tables.Single().Rows.Single();
        Assert.Equal(["10000", "15000", "0", "0", "0", "25000", "24500", "-500"], row.Skip(3));
        Assert.Equal("2025-11-16 21:33", row[1]);
    }

    [Fact]
    public async Task Reports_need_the_reports_capability_and_valid_ranges()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.rep", RoleTemplates.Cashier);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Reports.GenerateAsync(cashier, new ReportRequest(ReportType.DailySales, Day1, Day2))).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Reports.GetDashboardAsync(cashier, Day1)).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Reports.CloseBusinessDayAsync(cashier, Day1)).Status);

        Assert.Equal(ResultStatus.Validation, (await _h.Reports.GenerateAsync(_h.Owner, new ReportRequest(ReportType.DailySales, Day2, Day1))).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Reports.GenerateAsync(_h.Owner, new ReportRequest(ReportType.DailySales, "2024-01-01", "2025-11-17"))).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Reports.GenerateAsync(_h.Owner, new ReportRequest(ReportType.DailySales, "yesterday", Day2))).Status);
        Assert.Equal(ResultStatus.NotFound, (await _h.Reports.RenderAsync(_h.Owner, Guid.NewGuid(), ReportFormat.Csv)).Status);
    }

    [Fact]
    public void Exact_decimal_text_and_pdf_escaping()
    {
        Assert.Equal("-1,234,567.05", ReportValues.Decimal(-123_456_705, 2, thousands: true));
        Assert.Equal("0.07", ReportValues.Decimal(7, 2, thousands: false));
        Assert.Equal("15", ReportValues.Decimal(15, 0, thousands: true));
        Assert.Equal("1.5", ReportValues.Quantity(1500));
        Assert.Equal(@"a\(b\)\\ \361 ?", SimplePdf.Escape("a(b)\\ ñ ₱"));
    }
}
