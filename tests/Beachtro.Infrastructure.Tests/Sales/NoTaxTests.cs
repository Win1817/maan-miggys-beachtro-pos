using Beachtro.Application.Catalog;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>POS-DEC-009: the business charges no tax; switching keeps products selling and history intact.</summary>
public sealed class NoTaxTests : IAsyncLifetime
{
    private SalesHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await SalesHarness.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Switching_to_no_tax_keeps_products_selling_and_past_sales_unchanged()
    {
        var before = await _h.SellCashAsync((_h.Taxed, 1));                      // 35.00 with 12% included
        Assert.Equal(3_75L, _h.Security.Database.Scalar<long>($"SELECT TaxM FROM Orders WHERE Id = '{before.OrderId}'"));

        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        Ok(await _h.Security.Profiles.ApplyNoTaxPolicyAsync(_h.Owner, null));
        var profile = Ok(await _h.Security.Profiles.GetEffectiveCalculationProfileAsync(_h.Security.Clock.UtcNowMilliseconds));
        Assert.True(profile.IsNoTax);
        Assert.Equal(["NONE", "SYN"], profile.TaxRules.Keys.Order(StringComparer.Ordinal));

        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        var after = await _h.SellCashAsync((_h.Taxed, 1), (_h.Tea, 1));         // same products, same prices, no tax
        Assert.Equal((0L, 135_00L), (
            _h.Security.Database.Scalar<long>($"SELECT TaxM FROM Orders WHERE Id = '{after.OrderId}'"),
            _h.Security.Database.Scalar<long>($"SELECT TotalM FROM Orders WHERE Id = '{after.OrderId}'")));
        Assert.Equal(2L, _h.Count("OrderItems", $"OrderId = '{after.OrderId}' AND TreatmentCode = 'NotTaxed'"));
        Assert.Equal(0L, _h.Count("ItemTaxComponents", $"OrderItemId IN (SELECT Id FROM OrderItems WHERE OrderId = '{after.OrderId}')"));

        // The earlier sale keeps the tax it was made with.
        Assert.Equal(3_75L, _h.Security.Database.Scalar<long>($"SELECT TaxM FROM Orders WHERE Id = '{before.OrderId}'"));

        // Applying again changes nothing.
        Assert.Equal(profile.Profile.Id, Ok(await _h.Security.Profiles.ApplyNoTaxPolicyAsync(_h.Owner, null)));
    }

    [Fact]
    public async Task A_new_product_without_a_tax_key_gets_the_no_tax_entry()
    {
        var category = Ok(await _h.Catalog.ListCategoriesAsync(_h.Owner, includeInactive: false))[0].Id;
        var input = new ProductInput(null, category, "TEST-NEW", "New item", null, 60_00, 1000, false, null, true, null);

        // While a taxed profile is in effect, there is no no-tax entry to use.
        Assert.False((await _h.Catalog.SaveProductAsync(_h.Owner, input)).IsSuccess);

        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        Ok(await _h.Security.Profiles.ApplyNoTaxPolicyAsync(_h.Owner, null));
        Ok(await _h.Catalog.SaveProductAsync(_h.Owner, input));
        Assert.Equal("NONE", _h.Security.Database.Scalar<string>("SELECT TaxKey FROM Products WHERE Sku = 'TEST-NEW'"));
    }

    [Fact]
    public void Not_taxed_lines_compute_no_tax_under_an_approved_profile()
    {
        var profile = new CalculationProfile(Guid.NewGuid(), 1, ProfileApprovalState.Approved, Currency.Php, OrderCalculator.StrategyKey);
        var line = new CartLine(1, Guid.NewGuid(), 99_50, 2000, 1000, Currency.Php, true, LineTaxRule.NotTaxed);
        var calc = OrderCalculator.Calculate(profile, [line], OrderDiscount.Fixed(10_00));
        Assert.Equal((189_00L, 0L, 189_00L), (calc.NetMinor, calc.TaxMinor, calc.TotalMinor));
    }
}
