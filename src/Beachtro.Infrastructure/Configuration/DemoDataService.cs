using Beachtro.Application.Catalog;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Runtime;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Configuration;

/// <summary>
/// Test-profile sample data (POS-DEC-005). Every name says TEST or is obviously synthetic; the 12%
/// rate is an arithmetic sample only, not a tax setting. Safe to run more than once.
/// </summary>
public sealed class DemoDataService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IProfileService profiles,
    ICatalogService catalog,
    IClock clock,
    LocalDataPaths paths) : IDemoDataService
{
    private static readonly (string Sku, string Name, string Category, long Price, string TaxKey, int? Stock)[] Products =
    [
        ("TEST-ICED-TEA", "Iced Tea (sample)", "Drinks (sample)", 95_00, "NONE", 40),
        ("TEST-CALAMANSI", "Calamansi Juice (sample)", "Drinks (sample)", 85_00, "NONE", 30),
        ("TEST-COFFEE", "Iced Coffee (sample)", "Drinks (sample)", 120_00, "NONE", 30),
        ("TEST-SILOG", "Tapsilog (sample)", "Meals (sample)", 180_00, "NONE", 20),
        ("TEST-FRIES", "Fries (sample)", "Snacks (sample)", 75_00, "NONE", 25),
        ("TEST-CORKAGE", "Corkage (sample, no stock)", "Snacks (sample)", 50_00, "NONE", null),
    ];

    public async Task<Result<DemoDataResult>> LoadSampleDataAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        if (paths.Profile != RuntimeProfile.Test)
        {
            return Result<DemoDataResult>.Fail(ResultStatus.Forbidden, "Sample data can only be loaded in the Test profile.");
        }

        var auth = await identity.AuthorizeAsync(session, Capabilities.SettingsFinancial, true, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<DemoDataResult>();
        }

        var now = clock.UtcNowMilliseconds;
        var profilesCreated = 0;
        // No taxation (POS-DEC-009): the sample profile charges no tax, and running the loader again
        // switches an older taxed sample profile to no tax.
        var before = await profiles.GetEffectiveCalculationProfileAsync(now, cancellationToken).ConfigureAwait(false);
        var noTax = await profiles.ApplyNoTaxPolicyAsync(session, "Sample data (Test profile)", cancellationToken).ConfigureAwait(false);
        if (!noTax.IsSuccess)
        {
            return noTax.As<DemoDataResult>();
        }

        if (!before.IsSuccess || before.Value!.Profile.Id != noTax.Value)
        {
            profilesCreated++;
        }

        if (!(await profiles.GetEffectiveCalendarProfileAsync(now, cancellationToken).ConfigureAwait(false)).IsSuccess)
        {
            var created = await profiles.CreateCalendarProfileAsync(session, new CalendarProfileDraft(ProfileApprovalState.Test, now, "Asia/Manila", 0, null), cancellationToken)
                .ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return created.As<DemoDataResult>();
            }

            profilesCreated++;
        }

        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await db.InstallationState.Select(s => s.CurrentBusinessProfileId).SingleAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                var created = await profiles.SaveBusinessProfileAsync(session,
                    new BusinessProfileDraft(ProfileApprovalState.Test, "TEST - MA'AN AND MIGGY'S SAMPLE", "TEST ADDRESS - NOT REAL", "TEST CONTACT", null, 1, null), cancellationToken)
                    .ConfigureAwait(false);
                if (!created.IsSuccess)
                {
                    return created.As<DemoDataResult>();
                }

                profilesCreated++;
            }
        }

        // Use each sample's tax key when the effective profile has it, otherwise a key that exists.
        var effective = await profiles.GetEffectiveCalculationProfileAsync(clock.UtcNowMilliseconds, cancellationToken).ConfigureAwait(false);
        if (!effective.IsSuccess || effective.Value!.TaxRules.Count == 0)
        {
            return Result<DemoDataResult>.Fail(ResultStatus.Validation, "The current calculation profile has no tax entries to assign to sample products.");
        }

        var taxKeys = effective.Value.TaxRules.Keys.Order(StringComparer.Ordinal).ToArray();
        string TaxKeyFor(string preferred) => effective.Value.TaxRules.ContainsKey(preferred) ? preferred : taxKeys[0];

        var productsCreated = 0;
        var categoryIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var p in Products)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (await db.Products.AnyAsync(x => x.NormalizedSku == p.Sku, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (!categoryIds.TryGetValue(p.Category, out var categoryId))
            {
                var normalized = p.Category.ToUpperInvariant();
                var existing = await db.Categories.Where(c => c.NormalizedName == normalized).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (existing is { } id)
                {
                    categoryId = id;
                }
                else
                {
                    var created = await catalog.SaveCategoryAsync(session, new CategoryInput(null, p.Category, null, true, null), cancellationToken).ConfigureAwait(false);
                    if (!created.IsSuccess)
                    {
                        return created.As<DemoDataResult>();
                    }

                    categoryId = created.Value;
                }

                categoryIds[p.Category] = categoryId;
            }

            var product = await catalog.SaveProductAsync(session, new ProductInput(null, categoryId, p.Sku, p.Name, null, p.Price, 1000, p.Stock is not null, TaxKeyFor(p.TaxKey), true, null), cancellationToken)
                .ConfigureAwait(false);
            if (!product.IsSuccess)
            {
                return product.As<DemoDataResult>();
            }

            if (p.Stock is { } units)
            {
                await catalog.AdjustStockAsync(session, new StockAdjustmentInput(Guid.NewGuid(), product.Value, units * 1000L, "sample opening stock", IsOpening: true), cancellationToken)
                    .ConfigureAwait(false);
            }

            productsCreated++;
        }

        return Result<DemoDataResult>.Ok(new DemoDataResult(profilesCreated, productsCreated));
    }
}
