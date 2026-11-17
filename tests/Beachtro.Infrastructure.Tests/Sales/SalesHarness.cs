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
using Beachtro.Infrastructure.Reports;
using Beachtro.Infrastructure.Sales;
using Beachtro.Infrastructure.Shifts;
using Beachtro.Infrastructure.Sync;
using Beachtro.Infrastructure.Tests.Sync;
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
    public RecoveryService Recovery { get; private set; } = null!;
    public ShiftService Shifts { get; private set; } = null!;
    public ReportService Reports { get; private set; } = null!;
    public ArtifactMaterializer Materializer { get; private set; } = null!;
    public FakeRemoteStore Remote { get; private set; } = new();
    public SyncService Sync { get; private set; } = null!;
    public JobScheduler Scheduler { get; private set; } = null!;

    /// <summary>Free disk space reported to backups; -1 means unknown (not limited).</summary>
    public long FreeSpaceBytes { get; set; } = -1;
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
        var h = Wire(new SalesHarness());
        var s = h.Security;
        h.Owner = await s.BootstrapOwnerAsync();
        await SeedAsync(h);
        return h;
    }

    /// <summary>
    /// A brand-new installation on a replacement PC (recovery drill): its own empty data folder, no owner,
    /// the same remote destination as the lost PC, and a clock set to <paramref name="nowUtc"/>.
    /// </summary>
    public static SalesHarness CreateBlankDevice(FakeRemoteStore remote, long nowUtc)
    {
        var h = new SalesHarness { Remote = remote };
        h.Clock.Advance(TimeSpan.FromMilliseconds(nowUtc - h.Clock.UtcNowMilliseconds));
        return Wire(h);
    }

    /// <summary>Signs the backup's owner in on this device (after a restore).</summary>
    public async Task SignInOwnerAsync() => Owner = await Security.SignInAsync(OwnerUsername, OwnerPassword);

    private static SalesHarness Wire(SalesHarness h)
    {
        var s = h.Security;
        h.Numbers = new NumberSeriesService(s.Database.Factory, s.Identity, s.Clock, s.Gate, s.Database.Paths);
        h.Catalog = new CatalogService(s.Database.Factory, s.Identity, s.Profiles, s.Clock, s.Gate);
        h.Checkout = new CheckoutService(s.Database.Factory, s.Identity, s.Profiles, h.Numbers, s.Clock, s.Gate);
        h.Corrections = new CorrectionService(s.Database.Factory, s.Identity, s.Profiles, s.Clock, s.Gate);
        h.Backups = new BackupService(s.Database.Factory, s.Identity, h.Numbers, s.Clock, s.Gate, s.Database.Paths, _ => h.FreeSpaceBytes, s.Hasher);
        h.Recovery = new RecoveryService(s.Database.Factory, s.Identity, s.Clock, s.Gate);
        h.Shifts = new ShiftService(s.Database.Factory, s.Identity, s.Clock, s.Gate);
        h.Reports = new ReportService(s.Database.Factory, s.Identity, s.Profiles, s.Clock, s.Gate);
        h.Materializer = new ArtifactMaterializer(s.Database.Factory, s.Clock, s.Gate, s.Database.Paths);
        h.Sync = h.NewSyncWorker();
        h.Scheduler = new JobScheduler(s.Database.Factory, s.Profiles, s.Clock, s.Gate, h.Backups, h.Reports);
        return h;
    }

    private static async Task SeedAsync(SalesHarness h)
    {
        var s = h.Security;
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

    /// <summary>Another upload worker over the same database (its own lease owner), always using the fake remote.</summary>
    public SyncService NewSyncWorker() =>
        new(Security.Database.Factory, Security.Identity, Clock, Security.Gate, Security.Database.Paths, Materializer, new FakeRemoteFactory(Remote), () => 1.0);

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

    public void Dispose()
    {
        Sync.Dispose();
        Scheduler.Dispose();
        Security.Dispose();
    }
}
