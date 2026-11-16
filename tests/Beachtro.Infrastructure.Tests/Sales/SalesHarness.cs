using Beachtro.Application.Catalog;
using Beachtro.Application.Configuration;
using Beachtro.Application.Sales;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Catalog;
using Beachtro.Infrastructure.Numbering;
using Beachtro.Infrastructure.Recovery;
using Beachtro.Infrastructure.Sales;
using Beachtro.Infrastructure.Tests.Security;
using static Beachtro.Infrastructure.Tests.Security.SecurityHarness;

namespace Beachtro.Infrastructure.Tests.Sales;

/// <summary>
/// Synthetic store: Test calculation profile (NONE = TestNoTax, SYN = 12% inclusive arithmetic
/// input), Asia/Manila calendar, TEST business profile, and products with opening stock.
/// </summary>
public sealed class SalesHarness : IDisposable
{
    public const int SyntheticRate = 120_000;

    private SalesHarness()
    {
    }

    public SecurityHarness Security { get; } = new();
    public ManualClock Clock => Security.Clock;
    public NumberSeriesService Numbers { get; private set; } = null!;
    public CatalogService Catalog { get; private set; } = null!;
    public CheckoutService Checkout { get; private set; } = null!;
    public CorrectionService Corrections { get; private set; } = null!;
    public BackupService Backups { get; private set; } = null!;
    public UserSession Owner { get; private set; } = null!;
    public Guid CategoryId { get; private set; }

    /// <summary>100.00 iced tea, no tax, stock 10.</summary>
    public Guid Tea { get; private set; }

    /// <summary>50.00 cookie, no tax, stock 10.</summary>
    public Guid Cookie { get; private set; }

    /// <summary>35.00 taxed item, 12% inclusive synthetic, stock 10.</summary>
    public Guid Taxed { get; private set; }

    /// <summary>20.00 untracked item.</summary>
    public Guid Service { get; private set; }

    public static async Task<SalesHarness> CreateAsync()
    {
        var h = new SalesHarness();
        var s = h.Security;
        h.Numbers = new NumberSeriesService(s.Database.Factory, s.Identity, s.Clock, s.Gate, s.Database.Paths);
        h.Catalog = new CatalogService(s.Database.Factory, s.Identity, s.Profiles, s.Clock, s.Gate);
        h.Checkout = new CheckoutService(s.Database.Factory, s.Identity, s.Profiles, h.Numbers, s.Clock, s.Gate);
        h.Corrections = new CorrectionService(s.Database.Factory, s.Identity, s.Profiles, s.Clock, s.Gate);
        h.Backups = new BackupService(s.Database.Factory, s.Identity, h.Numbers, s.Clock, s.Gate, s.Database.Paths);

        h.Owner = await s.BootstrapOwnerAsync();
        var now = s.Clock.UtcNowMilliseconds;
        Ok(await s.Profiles.CreateCalculationProfileAsync(h.Owner, new CalculationProfileDraft(ProfileApprovalState.Test, now, Currency.Php,
        [
            new TaxConfigurationDraft("NONE", 0, PriceMode.Inclusive, TreatmentCode.TestNoTax),
            new TaxConfigurationDraft("SYN", SyntheticRate, PriceMode.Inclusive, TreatmentCode.Taxable),
        ], null)));
        Ok(await s.Profiles.CreateCalendarProfileAsync(h.Owner, new CalendarProfileDraft(ProfileApprovalState.Test, now, "Asia/Manila", 0, null)));
        Ok(await s.Profiles.SaveBusinessProfileAsync(h.Owner, new BusinessProfileDraft(ProfileApprovalState.Test, "TEST CAFE - NOT REAL", "TEST ADDRESS", null, null, 1, null)));

        h.CategoryId = Ok(await h.Catalog.SaveCategoryAsync(h.Owner, new CategoryInput(null, "Test Items", null, true, null)));
        h.Tea = await h.AddProductAsync("TEST-TEA", "Synthetic Iced Tea", 100_00, "NONE", stockUnits: 10);
        h.Cookie = await h.AddProductAsync("TEST-COOKIE", "Synthetic Cookie", 50_00, "NONE", stockUnits: 10);
        h.Taxed = await h.AddProductAsync("TEST-TAXED", "Synthetic Taxed Item", 35_00, "SYN", stockUnits: 10);
        h.Service = await h.AddProductAsync("TEST-SERVICE", "Synthetic Untracked", 20_00, "NONE", stockUnits: null);
        return h;
    }

    public async Task<Guid> AddProductAsync(string sku, string name, long price, string taxKey, int? stockUnits)
    {
        var id = Ok(await Catalog.SaveProductAsync(Owner, new ProductInput(null, CategoryId, sku, name, null, price, 1000, stockUnits is not null, taxKey, true, null)));
        if (stockUnits is { } units)
        {
            Ok(await Catalog.AdjustStockAsync(Owner, new StockAdjustmentInput(Guid.NewGuid(), id, units * 1000L, "opening count", IsOpening: true)));
        }

        return id;
    }

    public static CartInput Cart(params (Guid Product, int Units)[] lines) =>
        new(OrderType.DineIn, lines.Select(l => new CartLineInput(l.Product, l.Units * 1000L)).ToArray());

    /// <summary>Prepares and completes a sale in one go; returns the receipt.</summary>
    public async Task<SaleReceipt> SellAsync(UserSession cashier, CartInput cart, PaymentInput payment)
    {
        var requestId = Guid.NewGuid();
        Ok(await Checkout.PrepareAsync(cashier, requestId, cart));
        return Ok(await Checkout.CompleteAsync(cashier, requestId, payment));
    }

    public Task<SaleReceipt> SellCashAsync(params (Guid Product, int Units)[] lines) =>
        SellAsync(Owner, Cart(lines), new CashPayment(1_000_000_00));

    public long Stock(Guid product) =>
        Security.Database.Scalar<long>($"SELECT QuantityQ FROM Inventory WHERE ProductId = '{product}'");

    public long Count(string table, string where = "1 = 1") =>
        Security.Database.Scalar<long>($"SELECT count(*) FROM {table} WHERE {where}");

    /// <summary>Keeps the owner's sensitive-action window open after clock advances.</summary>
    public async Task ReauthOwnerAsync() => Ok(await Security.Identity.ReauthenticateAsync(Owner, OwnerPassword));

    public void Dispose() => Security.Dispose();
}
