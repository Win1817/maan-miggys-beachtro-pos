using Beachtro.Application.Catalog;
using Beachtro.Application.Common;
using Beachtro.Application.Security;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>T-03 catalog and T-05 stock (FR-004–008).</summary>
public sealed class CatalogTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private ProductInput TeaInput(int version, long price) =>
        new(_h.Tea, _h.CategoryId, "TEST-TEA", "Synthetic Iced Tea", null, price, 1000, true, "NONE", true, version);

    [Fact]
    public async Task Skus_are_unique_after_normalization_and_tax_keys_must_exist()
    {
        var duplicate = await _h.Catalog.SaveProductAsync(_h.Owner, new ProductInput(null, _h.CategoryId, " test-tea ", "Dup", null, 1_00, 1000, false, "NONE", true, null));
        Assert.Equal(ResultStatus.Conflict, duplicate.Status);

        var unknownTax = await _h.Catalog.SaveProductAsync(_h.Owner, new ProductInput(null, _h.CategoryId, "NEW-1", "New", null, 1_00, 1000, false, "VAT", true, null));
        Assert.Equal(ResultStatus.Validation, unknownTax.Status);

        var zeroPrice = await _h.Catalog.SaveProductAsync(_h.Owner, new ProductInput(null, _h.CategoryId, "NEW-2", "New", null, 0, 1000, false, "NONE", true, null));
        Assert.Equal(ResultStatus.Validation, zeroPrice.Status);
    }

    [Fact]
    public async Task Edits_need_the_current_version_and_price_changes_are_audited()
    {
        Ok(await _h.Catalog.SaveProductAsync(_h.Owner, TeaInput(1, 120_00)));
        Assert.Equal(ResultStatus.Conflict, (await _h.Catalog.SaveProductAsync(_h.Owner, TeaInput(1, 130_00))).Status);

        var audit = _h.Security.Database.Scalar<string>(
            $"SELECT SafeDetailsJson FROM AuditLogs WHERE Action = 'Catalog.ProductSaved' AND EntityId = '{_h.Tea}' ORDER BY OccurredUtc DESC, rowid DESC LIMIT 1");
        Assert.Contains("\"priceBefore\":10000", audit, StringComparison.Ordinal);
        Assert.Contains("\"priceAfter\":12000", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stock_changes_need_a_reason_stay_non_negative_and_reconcile_with_movements()
    {
        Assert.Equal(ResultStatus.Validation, (await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, 1000, " ", false))).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, 500, "half", false))).Status);
        Assert.Equal(ResultStatus.InsufficientStock, (await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, -11_000, "count", false))).Status);
        Assert.Equal(ResultStatus.Conflict, (await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, 1000, "again", true))).Status);
        Assert.Equal(ResultStatus.Validation, (await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Service, 1000, "untracked", false))).Status);

        Assert.Equal(7000, Ok(await _h.Catalog.AdjustStockAsync(_h.Owner, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, -3000, "spoiled", false))));
        Assert.Equal(_h.Stock(_h.Tea), _h.Security.Database.Scalar<long>($"SELECT sum(DeltaQ) FROM InventoryMovements WHERE ProductId = '{_h.Tea}'"));
    }

    [Fact]
    public async Task Repeating_a_stock_request_does_not_apply_it_twice()
    {
        var request = new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, 2000, "delivery", false);
        Assert.Equal(12_000, Ok(await _h.Catalog.AdjustStockAsync(_h.Owner, request)));
        Assert.Equal(12_000, Ok(await _h.Catalog.AdjustStockAsync(_h.Owner, request)));
        Assert.Equal(12_000, _h.Stock(_h.Tea));
        Assert.Equal(ResultStatus.Conflict, (await _h.Catalog.AdjustStockAsync(_h.Owner, request with { DeltaQ = 3000 })).Status);
    }

    [Fact]
    public async Task Negative_override_needs_its_own_capability()
    {
        var (_, manager) = await _h.Security.CreateUserAsync(_h.Owner, "manager.one", RoleTemplates.Manager);
        var overrideInput = new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, -12_000, "count correction", false, AllowNegative: true);

        Assert.Equal(ResultStatus.Forbidden, (await _h.Catalog.AdjustStockAsync(manager, overrideInput)).Status);
        Assert.Equal(-2000, Ok(await _h.Catalog.AdjustStockAsync(_h.Owner, overrideInput)));
        Assert.Equal(1L, _h.Count("AuditLogs", "Action = 'Inventory.NegativeOverride'"));
    }

    [Fact]
    public async Task Cashiers_cannot_manage_the_catalog_or_stock()
    {
        var (_, cashier) = await _h.Security.CreateUserAsync(_h.Owner, "cashier.one", RoleTemplates.Cashier);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Catalog.SaveProductAsync(cashier, TeaInput(1, 1_00))).Status);
        Assert.Equal(ResultStatus.Forbidden, (await _h.Catalog.AdjustStockAsync(cashier, new StockAdjustmentInput(Guid.NewGuid(), _h.Tea, 1000, "x", false))).Status);
        Assert.True((await _h.Catalog.SearchProductsAsync(cashier, "tea", false)).IsSuccess);
    }
}
