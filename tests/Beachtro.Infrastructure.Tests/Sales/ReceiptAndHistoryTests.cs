using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Receipts;
using Beachtro.Application.Runtime;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Configuration;
using Beachtro.Infrastructure.Receipts;
using Beachtro.Infrastructure.Runtime;
using Beachtro.Infrastructure.Sales;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>Records what would have been spooled; can be told to refuse or throw.</summary>
public sealed class FakePrinter : IReceiptPrinter
{
    public List<(ReceiptDocument Document, string Printer)> Printed { get; } = [];
    public bool Refuse { get; set; }
    public bool Throw { get; set; }

    public IReadOnlyList<string> InstalledPrinters() => ["Fake Receipt Printer"];

    public Task<PrintSubmission> PrintAsync(ReceiptDocument document, string printerName, CancellationToken cancellationToken = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("spooler crashed");
        }

        if (Refuse)
        {
            return Task.FromResult(new PrintSubmission(false, null, "Printer offline"));
        }

        Printed.Add((document, printerName));
        return Task.FromResult(new PrintSubmission(true, "spool-" + Printed.Count, null));
    }
}

/// <summary>T-08 receipts/print jobs, FR-017 history scope and the Test-only sample data loader.</summary>
public sealed class ReceiptAndHistoryTests : IAsyncLifetime
{
    private SalesHarness _h = null!;
    private FakePrinter _printer = null!;
    private PrintService _print = null!;
    private OrderQueryService _orders = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await SalesHarness.CreateAsync();
        var s = _h.Security;
        _printer = new FakePrinter();
        _print = new PrintService(s.Database.Factory, s.Identity, _printer, s.Clock, s.Gate);
        _orders = new OrderQueryService(s.Database.Factory, s.Identity);
    }

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SelectPrinterAsync() =>
        Ok(await _h.Security.Profiles.SetOperationalSettingAsync(_h.Owner, OperationalSettingKeys.PrinterName, "\"Fake Receipt Printer\""));

    [Fact]
    public async Task Sale_receipt_is_rendered_from_the_snapshot_with_test_markers()
    {
        var sale = await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Tea, 2), (_h.Taxed, 1)) with { Discount = OrderDiscount.Fixed(5_00) }, new CashPayment(300_00));
        var receipt = Ok(await _print.RenderSaleAsync(_h.Owner, sale.OrderId));

        Assert.All(receipt.Lines, l => Assert.True(l.Length <= ReceiptRenderer.Width, $"Line too wide: '{l}'"));
        Assert.True(receipt.IsTest);
        Assert.Contains(receipt.Lines, l => l.Contains(ReceiptRenderer.TestBanner, StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.Contains("TEST CAFE - NOT REAL", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("Sale", StringComparison.Ordinal) && l.EndsWith("MB-20251116-000001", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("Date", StringComparison.Ordinal) && l.EndsWith("2025-11-16 21:33", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("  2 x 100.00", StringComparison.Ordinal) && l.EndsWith("200.00", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("Subtotal", StringComparison.Ordinal) && l.EndsWith("235.00", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("Discount", StringComparison.Ordinal) && l.EndsWith("-5.00", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("TOTAL", StringComparison.Ordinal) && l.EndsWith("PHP 230.00", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("Change", StringComparison.Ordinal) && l.EndsWith("70.00", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("Incl. tax SYN 12%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receipts_do_not_change_when_the_catalog_changes_later()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        var before = Ok(await _print.RenderSaleAsync(_h.Owner, sale.OrderId)).Text;
        Ok(await _h.Catalog.SaveProductAsync(_h.Owner, new Application.Catalog.ProductInput(_h.Tea, _h.CategoryId, "TEST-TEA", "Renamed Tea", null, 999_00, 1000, true, "NONE", true, 1)));
        Assert.Equal(before, Ok(await _print.RenderSaleAsync(_h.Owner, sale.OrderId)).Text);
    }

    [Fact]
    public async Task Original_prints_once_then_reprints_need_a_reason_and_are_marked()
    {
        await SelectPrinterAsync();
        var sale = await _h.SellCashAsync((_h.Tea, 1));

        var original = Ok(await _print.PrintOriginalAsync(_h.Owner, sale.OrderId, null));
        Assert.Equal(PrintJobState.SpoolAccepted, original.State);
        Assert.Equal(ResultStatus.Conflict, (await _print.PrintOriginalAsync(_h.Owner, sale.OrderId, null)).Status);

        Assert.Equal(ResultStatus.Validation, (await _print.ReprintAsync(_h.Owner, sale.OrderId, null, " ")).Status);
        var reprint = Ok(await _print.ReprintAsync(_h.Owner, sale.OrderId, null, "customer asked for a copy"));
        Assert.True(reprint.Document!.IsReprint);
        Assert.Contains(reprint.Document.Lines, l => l.Contains(ReceiptRenderer.ReprintBanner, StringComparison.Ordinal));
        Assert.Equal(2, _printer.Printed.Count);
        Assert.Equal(1L, _h.Count("PrintJobs", "Purpose = 'Reprint' AND Reason = 'customer asked for a copy'"));

        Ok(await _print.ConfirmAsync(_h.Owner, original.JobId, printed: true));
        Assert.Equal(1L, _h.Count("PrintJobs", $"Id = '{original.JobId}' AND State = 'OperatorConfirmed'"));
    }

    [Fact]
    public async Task Printer_failures_are_recorded_and_the_original_can_be_retried()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        var noPrinter = Ok(await _print.PrintOriginalAsync(_h.Owner, sale.OrderId, null));
        Assert.Equal(PrintJobState.Failed, noPrinter.State);
        Assert.Contains("No receipt printer", noPrinter.LastError, StringComparison.Ordinal);

        await SelectPrinterAsync();
        _printer.Refuse = true;
        Assert.Equal(PrintJobState.Failed, Ok(await _print.PrintOriginalAsync(_h.Owner, sale.OrderId, null)).State);
        _printer.Throw = true;
        Assert.Equal(PrintJobState.Failed, Ok(await _print.PrintOriginalAsync(_h.Owner, sale.OrderId, null)).State);

        _printer.Refuse = _printer.Throw = false;
        var retried = Ok(await _print.PrintOriginalAsync(_h.Owner, sale.OrderId, null));
        Assert.Equal(PrintJobState.SpoolAccepted, retried.State);
        Assert.Equal(4, retried.AttemptCount);
        Assert.Equal(1L, _h.Count("Orders")); // printing never touches the sale
    }

    [Fact]
    public async Task Interrupted_submissions_become_outcome_unknown_on_restart()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        _h.Security.Database.Execute($"UPDATE PrintJobs SET State = 'Submitting' WHERE OrderId = '{sale.OrderId}'");

        Assert.Equal(1, await _print.RecoverInterruptedAsync());
        Assert.Equal(1L, _h.Count("PrintJobs", "State = 'OutcomeUnknown'"));
        Ok(await _print.ConfirmAsync(_h.Owner, Guid.Parse(_h.Security.Database.Scalar<string>("SELECT Id FROM PrintJobs")), printed: false));
        Assert.Equal(1L, _h.Count("PrintJobs", "State = 'Failed'"));
    }

    [Fact]
    public async Task Test_print_has_no_financial_source()
    {
        await SelectPrinterAsync();
        var job = Ok(await _print.TestPrintAsync(_h.Owner));
        Assert.Equal(PrintPurpose.Test, job.Purpose);
        Assert.Equal(1L, _h.Count("PrintJobs", "Purpose = 'Test' AND IsTestModel = 1 AND OrderId IS NULL AND CorrectionId IS NULL"));
    }

    [Fact]
    public async Task Refund_receipts_reference_the_original_sale()
    {
        await SelectPrinterAsync();
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var detail = Ok(await _orders.GetAsync(_h.Owner, sale.OrderId));
        var requestId = Guid.NewGuid();
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, requestId,
            new CorrectionInput(sale.OrderId, CorrectionKind.Refund, [new CorrectionLineInput(detail.Lines[0].OrderItemId, 1000, StockDisposition.Return)], "spilled")));
        var refund = Ok(await _h.Corrections.CompleteAsync(_h.Owner, requestId, new CashPaidOut()));

        var receipt = Ok(await _print.RenderCorrectionAsync(_h.Owner, refund.CorrectionId));
        Assert.Contains(receipt.Lines, l => l.Trim() == "REFUND");
        Assert.Contains(receipt.Lines, l => l.EndsWith("MB-20251116-000001", StringComparison.Ordinal) && l.StartsWith("Original sale", StringComparison.Ordinal));
        Assert.Contains(receipt.Lines, l => l.StartsWith("TOTAL REFUND", StringComparison.Ordinal) && l.EndsWith("PHP 100.00", StringComparison.Ordinal));
        Assert.Equal(PrintJobState.SpoolAccepted, Ok(await _print.PrintOriginalAsync(_h.Owner, null, refund.CorrectionId)).State);

        var after = Ok(await _orders.GetAsync(_h.Owner, sale.OrderId));
        Assert.Equal(1000, after.Lines[0].RemainingQ);
        Assert.Single(after.Corrections);
        Assert.Equal(100_00, after.Summary.CorrectedTotalMinor);
    }

    [Fact]
    public async Task Cashiers_see_only_their_own_sales_in_history_and_receipts()
    {
        var (_, cashierA) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.a", RoleTemplates.Cashier);
        var (_, cashierB) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.b", RoleTemplates.Cashier);
        var saleA = await _h.SellAsync(cashierA, SalesHarness.Cart((_h.Tea, 1)), new CashPayment(100_00));
        await _h.SellAsync(cashierB, SalesHarness.Cart((_h.Cookie, 1)), new CashPayment(50_00));

        Assert.Single(Ok(await _orders.ListAsync(cashierA, null, null)));
        Assert.Equal(2, Ok(await _orders.ListAsync(_h.Owner, "2025-11-16", null)).Count);
        Assert.Equal(ResultStatus.NotFound, (await _orders.GetAsync(cashierB, saleA.OrderId)).Status);
        Assert.Equal(ResultStatus.NotFound, (await _print.RenderSaleAsync(cashierB, saleA.OrderId)).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _print.ReprintAsync(cashierA, saleA.OrderId, null, "copy")).Status);
        Assert.Single(Ok(await _orders.ListAsync(_h.Owner, null, "000001")));
    }

    [Fact]
    public async Task Sample_data_loads_only_in_the_test_profile_and_is_repeatable()
    {
        var harness = _h.Security;
        var demo = new DemoDataService(harness.Database.Factory, harness.Identity, harness.Profiles, _h.Catalog, harness.Clock, harness.Database.Paths);
        var first = Ok(await demo.LoadSampleDataAsync(_h.Owner));
        Assert.Equal(6, first.ProductsCreated);
        Assert.Equal(0, first.ProfilesCreated); // the harness already configured profiles
        Assert.Equal(0, Ok(await demo.LoadSampleDataAsync(_h.Owner)).ProductsCreated);
        // The harness profile has NONE and SYN only, so samples fall back to an existing key.
        Assert.Equal(1L, _h.Count("Products", "Sku = 'TEST-SILOG' AND TaxKey IN ('NONE', 'SYN')"));

        var production = new DemoDataService(harness.Database.Factory, harness.Identity, harness.Profiles, _h.Catalog, harness.Clock,
            new LocalDataPaths(harness.Database.Paths.Root, RuntimeProfile.Production));
        Assert.Equal(ResultStatus.Forbidden, (await production.LoadSampleDataAsync(_h.Owner)).Status);
    }
}
