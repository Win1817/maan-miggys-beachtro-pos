using Beachtro.Application.Catalog;
using Beachtro.Application.Common;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Serialization;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>T-06 checkout and T-07 idempotency/atomicity against real SQLite.</summary>
public sealed class CheckoutTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task F01_cash_sale_commits_the_whole_tuple_once()
    {
        var sale = await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Tea, 2), (_h.Cookie, 1)), new CashPayment(300_00));

        Assert.Equal("MB-20251116-000001", sale.TransactionNumber);
        Assert.Equal("2025-11-16", sale.BusinessDate);
        Assert.Equal(250_00, sale.TotalMinor);
        Assert.Equal((300_00L, 50_00L), (sale.TenderedMinor!.Value, sale.ChangeMinor!.Value));

        Assert.Equal(8000, _h.Stock(_h.Tea));
        Assert.Equal(9000, _h.Stock(_h.Cookie));
        Assert.Equal(2L, _h.Count("InventoryMovements", $"Type = 'Sale' AND OrderId = '{sale.OrderId}'"));
        Assert.Equal(2L, _h.Count("OrderItems", $"OrderId = '{sale.OrderId}'"));
        Assert.Equal(1L, _h.Count("Payments", $"OrderId = '{sale.OrderId}' AND AppliedM = 25000 AND TenderedM = 30000 AND ChangeM = 5000"));
        Assert.Equal(1L, _h.Count("ArtifactRecords", $"OrderId = '{sale.OrderId}' AND State = 'Intent' AND Type = 'SaleJson'"));
        Assert.Equal(1L, _h.Count("PrintJobs", $"OrderId = '{sale.OrderId}' AND Purpose = 'Original' AND State = 'Pending'"));
        Assert.Equal(1L, _h.Count("AuditLogs", $"Action = 'Sales.Completed' AND EntityId = '{sale.OrderId}'"));
        Assert.Equal(1L, _h.Count("CheckoutRequests", $"Id = '{sale.RequestId}' AND State = 'Completed' AND OrderId = '{sale.OrderId}'"));
    }

    [Fact]
    public async Task The_snapshot_is_canonical_hashed_and_agrees_with_the_relational_rows()
    {
        var sale = await _h.SellCashAsync((_h.Taxed, 3));
        var json = _h.Security.Database.Scalar<string>($"SELECT SnapshotJson FROM Orders WHERE Id = '{sale.OrderId}'");
        var sha = _h.Security.Database.Scalar<string>($"SELECT SnapshotSha256 FROM Orders WHERE Id = '{sale.OrderId}'");

        Assert.Equal(CanonicalJson.Sha256(json), sha);
        Assert.StartsWith("{\"schema\":\"sale.v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"transactionNumber\":\"MB-20251116-000001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"registeredName\":\"TEST CAFE - NOT REAL\"", json, StringComparison.Ordinal);
        // 3 × 35.00 inclusive at 12%: net 93.75, tax 11.25, total 105.00.
        Assert.Contains("\"totals\":{\"priceBasisSubtotalM\":10500,\"discountBasisM\":0,\"discountExTaxM\":0,\"netM\":9375,\"taxM\":1125,\"totalM\":10500}", json, StringComparison.Ordinal);
        Assert.Equal(1L, _h.Count("ItemTaxComponents", "ComponentKey = 'SYN' AND TaxM = 1125 AND RatePpm = 120000"));

        // A later price change never alters the stored sale.
        Ok(await _h.Catalog.SaveProductAsync(_h.Owner, new ProductInput(_h.Taxed, _h.CategoryId, "TEST-TAXED", "Renamed", null, 99_00, 1000, true, "SYN", true, 1)));
        Assert.Equal(json, _h.Security.Database.Scalar<string>($"SELECT SnapshotJson FROM Orders WHERE Id = '{sale.OrderId}'"));
    }

    [Fact]
    public async Task Numbers_are_sequential_per_business_date_and_journaled()
    {
        var a = await _h.SellCashAsync((_h.Tea, 1));
        var b = await _h.SellCashAsync((_h.Cookie, 1));
        _h.Clock.Advance(TimeSpan.FromDays(1));
        await _h.ReauthOwnerAsync();
        var c = await _h.SellCashAsync((_h.Cookie, 1));

        Assert.Equal(["MB-20251116-000001", "MB-20251116-000002", "MB-20251117-000001"], new[] { a, b, c }.Select(s => s.TransactionNumber));
        var journal = _h.Numbers.Journal.Read();
        Assert.True(journal.Trustworthy);
        Assert.Equal(3, journal.Records.Count);
        Assert.Equal([a.RequestId, b.RequestId, c.RequestId], journal.Records.Select(r => r.RequestId));
    }

    [Fact]
    public async Task Completing_twice_returns_the_same_sale_and_writes_nothing_more()
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));
        var first = Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00)));
        var second = Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(500_00)));

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(first.TransactionNumber, second.TransactionNumber);
        Assert.Equal(1L, _h.Count("Orders"));
        Assert.Equal(9000, _h.Stock(_h.Tea));
        Assert.Single(_h.Numbers.Journal.Read().Records);
    }

    [Fact]
    public async Task A_request_id_cannot_be_reused_for_a_different_cart()
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));
        Assert.True((await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1)))).IsSuccess);
        Assert.Equal(ResultStatus.Conflict, (await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 2)))).Status);
    }

    [Fact]
    public async Task A_price_change_after_prepare_makes_the_quote_stale_and_writes_nothing()
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));
        Ok(await _h.Catalog.SaveProductAsync(_h.Owner, new ProductInput(_h.Tea, _h.CategoryId, "TEST-TEA", "Synthetic Iced Tea", null, 110_00, 1000, true, "NONE", true, 1)));

        var result = await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(200_00));
        Assert.Equal(ResultStatus.StaleQuote, result.Status);
        Assert.Contains("11000", result.Message, StringComparison.Ordinal);
        Assert.Equal(0L, _h.Count("Orders"));
        Assert.Equal(10_000, _h.Stock(_h.Tea));
        Assert.Empty(_h.Numbers.Journal.Read().Records);
    }

    [Fact]
    public async Task Insufficient_stock_and_short_cash_are_refused_without_side_effects()
    {
        Assert.Equal(ResultStatus.InsufficientStock, (await _h.Checkout.PrepareAsync(_h.Owner, Guid.NewGuid(), SalesHarness.Cart((_h.Tea, 11)))).Status);

        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 2))));
        Assert.Equal(ResultStatus.Validation, (await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(199_99))).Status);

        // Stock taken by another sale after prepare is caught at completion.
        Ok(await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, -9000, "spoiled", false)));
        Assert.Equal(ResultStatus.InsufficientStock, (await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(200_00))).Status);
        Assert.Equal(0L, _h.Count("Orders"));
    }

    [Fact]
    public async Task Gcash_requires_manual_verification_and_flags_repeated_references()
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Cookie, 1))));
        Assert.Equal(ResultStatus.PaymentNotVerified, (await _h.Checkout.CompleteAsync(_h.Owner, requestId, new GCashPayment(false, "TEST-REF-1", null))).Status);

        var first = Ok(await _h.Checkout.CompleteAsync(_h.Owner, requestId, new GCashPayment(true, "TEST-REF-1", null)));
        Assert.Equal(PaymentMethod.GCash, first.Method);
        Assert.Null(first.ChangeMinor);
        Assert.Empty(first.Warnings);
        Assert.Equal(1L, _h.Count("Payments", $"OrderId = '{first.OrderId}' AND VerificationState = 'Verified' AND VerifiedByUserId = '{_h.Owner.UserId}'"));

        var second = await _h.SellAsync(_h.Owner, SalesHarness.Cart((_h.Cookie, 1)), new GCashPayment(true, "TEST-REF-1", null));
        Assert.Single(second.Warnings);
    }

    [Fact]
    public async Task Untracked_products_sell_without_stock_movements()
    {
        var sale = await _h.SellCashAsync((_h.Service, 3));
        Assert.Equal(60_00, sale.TotalMinor);
        Assert.Equal(0L, _h.Count("InventoryMovements", $"OrderId = '{sale.OrderId}'"));
    }

    [Fact]
    public async Task Discounts_need_the_discount_capability()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        var discounted = SalesHarness.Cart((_h.Tea, 1)) with { Discount = OrderDiscount.Percent(100_000) };

        Assert.Equal(ResultStatus.Forbidden, (await _h.Checkout.PrepareAsync(cashier, Guid.NewGuid(), discounted)).Status);
        var quote = Ok(await _h.Checkout.QuoteAsync(_h.Owner, discounted));
        Assert.Equal(90_00, quote.Calculation.TotalMinor);
    }

    [Fact]
    public async Task Abandoned_checkouts_cannot_complete_and_outcomes_are_queryable()
    {
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(_h.Owner, requestId, SalesHarness.Cart((_h.Tea, 1))));
        Assert.Equal(CheckoutState.Prepared, Ok(await _h.Checkout.GetOutcomeAsync(_h.Owner, requestId)).State);
        Assert.Equal(100_00, Ok(await _h.Checkout.GetOutcomeAsync(_h.Owner, requestId)).QuotedTotalMinor);

        Ok(await _h.Checkout.AbandonAsync(_h.Owner, requestId, "customer left"));
        Assert.Equal(CheckoutState.Abandoned, Ok(await _h.Checkout.GetOutcomeAsync(_h.Owner, requestId)).State);
        Assert.Equal(ResultStatus.Conflict, (await _h.Checkout.CompleteAsync(_h.Owner, requestId, new CashPayment(100_00))).Status);
        Assert.Equal(CheckoutState.NotFound, Ok(await _h.Checkout.GetOutcomeAsync(_h.Owner, Guid.NewGuid())).State);
    }

    [Fact]
    public async Task A_cashier_cannot_complete_or_inspect_another_cashiers_checkout()
    {
        var (_, cashierA) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.a", RoleTemplates.Cashier);
        var (_, cashierB) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.b", RoleTemplates.Cashier);
        var requestId = Guid.NewGuid();
        Ok(await _h.Checkout.PrepareAsync(cashierA, requestId, SalesHarness.Cart((_h.Tea, 1))));

        Assert.Equal(ResultStatus.Forbidden, (await _h.Checkout.CompleteAsync(cashierB, requestId, new CashPayment(100_00))).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Checkout.GetOutcomeAsync(cashierB, requestId)).Status);
        Assert.True((await _h.Checkout.CompleteAsync(cashierA, requestId, new CashPayment(100_00))).IsSuccess);
    }

    [Fact]
    public async Task Selling_is_refused_while_the_installation_is_in_recovery()
    {
        _h.Security.Database.Execute("UPDATE InstallationState SET Mode = 'RecoveryRequired'");
        Assert.Equal(ResultStatus.RecoveryRequired, (await _h.Checkout.PrepareAsync(_h.Owner, Guid.NewGuid(), SalesHarness.Cart((_h.Tea, 1)))).Status);
    }
}
