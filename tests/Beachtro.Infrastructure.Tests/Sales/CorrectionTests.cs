using Beachtro.Application.Common;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Serialization;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>T-10 corrections (FR-018): cumulative allocation, same tender, stock disposition, races.</summary>
public sealed class CorrectionTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private Guid ItemOf(SaleReceipt sale, int lineNo = 1) =>
        Guid.Parse(_h.Security.Database.Scalar<string>($"SELECT Id FROM OrderItems WHERE OrderId = '{sale.OrderId}' AND LineNo = {lineNo}"));

    private async Task<Result<CorrectionReceipt>> RefundAsync(UserSession session, SaleReceipt sale, long quantityQ, StockDisposition disposition, RefundEvidence evidence, int lineNo = 1)
    {
        var requestId = Guid.NewGuid();
        var prepared = await _h.Corrections.PrepareAsync(session, requestId,
            new CorrectionInput(sale.OrderId, CorrectionKind.Refund, [new CorrectionLineInput(ItemOf(sale, lineNo), quantityQ, disposition)], "customer returned item"));
        return prepared.IsSuccess ? await _h.Corrections.CompleteAsync(session, requestId, evidence) : prepared.As<CorrectionReceipt>();
    }

    [Fact]
    public async Task F07_three_unit_refunds_reverse_the_discounted_inclusive_sale_exactly()
    {
        var sale = await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Taxed, 3)) with { Discount = OrderDiscount.Fixed(5_00) }, new CashPayment(100_00));
        Assert.Equal(100_00, sale.TotalMinor);
        Assert.Equal(1L, _h.Count("Orders", "NetM = 8929 AND TaxM = 1071 AND DiscountExTaxM = 446"));

        var r1 = Ok(await RefundAsync(_h.Owner, sale, 1000, StockDisposition.Return, new CashPaidOut()));
        var r2 = Ok(await RefundAsync(_h.Owner, sale, 1000, StockDisposition.Return, new CashPaidOut()));
        var r3 = Ok(await RefundAsync(_h.Owner, sale, 1000, StockDisposition.Discard, new CashPaidOut()));

        Assert.Equal([33_33L, 33_33L, 33_34L], new[] { r1, r2, r3 }.Select(r => r.TotalMinor));
        Assert.Equal([29_76L, 29_76L, 29_77L], new[] { r1, r2, r3 }.Select(r => r.NetMinor));
        Assert.Equal([3_57L, 3_57L, 3_57L], new[] { r1, r2, r3 }.Select(r => r.TaxMinor));
        Assert.Equal(100_00L, _h.Security.Database.Scalar<long>($"SELECT sum(TotalM) FROM Corrections WHERE OriginalOrderId = '{sale.OrderId}'"));
        Assert.Equal(446L, _h.Security.Database.Scalar<long>($"SELECT sum(DiscountExTaxM) FROM Corrections WHERE OriginalOrderId = '{sale.OrderId}'"));

        // Two units returned to stock, one discarded; the original sale row is untouched.
        Assert.Equal(10_000 - 3000 + 2000, _h.Stock(_h.Taxed));
        Assert.Equal(1L, _h.Count("Orders", $"Id = '{sale.OrderId}' AND TotalM = 10000"));

        Assert.Equal(ResultStatus.Conflict, (await RefundAsync(_h.Owner, sale, 1000, StockDisposition.Return, new CashPaidOut())).Status);
    }

    [Fact]
    public async Task Correction_snapshot_and_rows_record_actor_approver_and_refund_evidence()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var receipt = Ok(await RefundAsync(_h.Owner, sale, 1000, StockDisposition.Return, new CashPaidOut()));

        Assert.StartsWith("RF-", receipt.CorrectionNumber, StringComparison.Ordinal);
        var json = _h.Security.Database.Scalar<string>($"SELECT SnapshotJson FROM Corrections WHERE Id = '{receipt.CorrectionId}'");
        Assert.Equal(CanonicalJson.Sha256(json), _h.Security.Database.Scalar<string>($"SELECT SnapshotSha256 FROM Corrections WHERE Id = '{receipt.CorrectionId}'"));
        Assert.Contains("\"originalTransactionNumber\":\"MB-20251116-000001\"", json, StringComparison.Ordinal);
        Assert.Equal(1L, _h.Count("RefundPayments", $"CorrectionId = '{receipt.CorrectionId}' AND Method = 'Cash' AND VerificationState = 'CashPaidOut' AND AmountM = 10000"));
        Assert.Equal(1L, _h.Count("Corrections", $"Id = '{receipt.CorrectionId}' AND ActorUserId = '{_h.Owner.UserId}' AND ApprovedByUserId = '{_h.Owner.UserId}'"));
        Assert.Equal(1L, _h.Count("InventoryMovements", "Type = 'Return' AND DeltaQ = 1000"));
        Assert.Equal(1L, _h.Count("AuditLogs", $"Action = 'Sales.RefundCompleted' AND EntityId = '{receipt.CorrectionId}'"));
    }

    [Fact]
    public async Task F11_two_prepared_refunds_for_the_last_unit_cannot_both_commit()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 1));
        var input = new CorrectionInput(sale.OrderId, CorrectionKind.Refund, [new CorrectionLineInput(ItemOf(sale), 1000, StockDisposition.Return)], "duplicate claim");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, a, input));
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, b, input));

        Ok(await _h.Corrections.CompleteAsync(_h.Owner, a, new CashPaidOut()));
        Assert.Equal(ResultStatus.Conflict, (await _h.Corrections.CompleteAsync(_h.Owner, b, new CashPaidOut())).Status);
        Assert.Equal(1L, _h.Count("Corrections"));
        Assert.Equal(10_000, _h.Stock(_h.Tea));
    }

    [Fact]
    public async Task Repeating_a_completed_correction_returns_it_without_a_second_refund()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var requestId = Guid.NewGuid();
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, requestId,
            new CorrectionInput(sale.OrderId, CorrectionKind.Refund, [new CorrectionLineInput(ItemOf(sale), 1000, StockDisposition.Return)], "return")));
        var first = Ok(await _h.Corrections.CompleteAsync(_h.Owner, requestId, new CashPaidOut()));
        var second = Ok(await _h.Corrections.CompleteAsync(_h.Owner, requestId, new CashPaidOut()));

        Assert.Equal(first.CorrectionId, second.CorrectionId);
        Assert.Equal(1L, _h.Count("Corrections"));
        Assert.Equal(9000, _h.Stock(_h.Tea));
    }

    [Fact]
    public async Task Refunds_go_back_by_the_original_tender()
    {
        var gcashSale = await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Cookie, 1)), new GCashPayment(true, null, null));
        Assert.Equal(ResultStatus.Validation, (await RefundAsync(_h.Owner, gcashSale, 1000, StockDisposition.Return, new CashPaidOut())).Status);

        var ok = Ok(await RefundAsync(_h.Owner, gcashSale, 1000, StockDisposition.Return, new GCashRefunded("TEST-REFUND-1")));
        Assert.Equal(1L, _h.Count("RefundPayments", $"CorrectionId = '{ok.CorrectionId}' AND Method = 'GCash' AND VerificationState = 'ManuallyRefunded' AND Reference = 'TEST-REFUND-1'"));
    }

    [Fact]
    public async Task Stock_disposition_must_match_how_the_item_was_sold()
    {
        var sale = await _h.SellCashAsync((_h.Service, 1), (_h.Tea, 1));
        Assert.Equal(ResultStatus.Validation, (await RefundAsync(_h.Owner, sale, 1000, StockDisposition.Return, new CashPaidOut(), lineNo: 1)).Status);
        Assert.Equal(ResultStatus.Validation, (await RefundAsync(_h.Owner, sale, 1000, StockDisposition.NotTracked, new CashPaidOut(), lineNo: 2)).Status);
        Assert.True((await RefundAsync(_h.Owner, sale, 1000, StockDisposition.NotTracked, new CashPaidOut(), lineNo: 1)).IsSuccess);
    }

    [Fact]
    public async Task A_void_reverses_everything_once_and_is_refused_after_a_partial_refund()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 2), (_h.Service, 1));
        var lines = new[]
        {
            new CorrectionLineInput(ItemOf(sale, 1), 2000, StockDisposition.Return),
            new CorrectionLineInput(ItemOf(sale, 2), 1000, StockDisposition.NotTracked),
        };

        var partialVoid = await _h.Corrections.PrepareAsync(_h.Owner, Guid.NewGuid(), new CorrectionInput(sale.OrderId, CorrectionKind.Void, [lines[0] with { QuantityQ = 1000 }, lines[1]], "wrong order"));
        Assert.Equal(ResultStatus.Validation, partialVoid.Status);

        var requestId = Guid.NewGuid();
        Ok(await _h.Corrections.PrepareAsync(_h.Owner, requestId, new CorrectionInput(sale.OrderId, CorrectionKind.Void, lines, "wrong order")));
        var voided = Ok(await _h.Corrections.CompleteAsync(_h.Owner, requestId, new CashPaidOut()));
        Assert.Equal(sale.TotalMinor, voided.TotalMinor);
        Assert.Equal(10_000, _h.Stock(_h.Tea));

        var other = await _h.SellCashAsync((_h.Tea, 2));
        Ok(await RefundAsync(_h.Owner, other, 1000, StockDisposition.Return, new CashPaidOut()));
        var voidAfterRefund = await _h.Corrections.PrepareAsync(_h.Owner, Guid.NewGuid(),
            new CorrectionInput(other.OrderId, CorrectionKind.Void, [new CorrectionLineInput(ItemOf(other), 2000, StockDisposition.Return)], "void"));
        Assert.Equal(ResultStatus.Conflict, voidAfterRefund.Status);
    }

    [Fact]
    public async Task Refund_and_void_authority_follow_the_capability_matrix()
    {
        var sale = await _h.SellCashAsync((_h.Tea, 2));
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        var (_, manager) = await _h.Security.CreateUserAsync(_h.Owner, "manager.one", RoleTemplates.Manager);

        Assert.Equal(ResultStatus.Forbidden, (await RefundAsync(cashier, sale, 1000, StockDisposition.Return, new CashPaidOut())).Status);
        Assert.True((await RefundAsync(manager, sale, 1000, StockDisposition.Return, new CashPaidOut())).IsSuccess);

        var voidInput = new CorrectionInput(sale.OrderId, CorrectionKind.Void, [new CorrectionLineInput(ItemOf(sale), 2000, StockDisposition.Return)], "void");
        Assert.Equal(ResultStatus.Forbidden, (await _h.Corrections.PrepareAsync(manager, Guid.NewGuid(), voidInput)).Status);
    }
}
