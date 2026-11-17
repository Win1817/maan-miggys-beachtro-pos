using Beachtro.Application.Catalog;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Ports;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Catalog;

/// <summary>
/// Catalog and stock (FR-004–008; IB-07). Edits use optimistic versions; stock changes write the
/// balance, an immutable movement and an audit row in one transaction.
/// </summary>
public sealed class CatalogService(
    IDbContextFactory<PosDbContext> contextFactory,
    IIdentityService identity,
    IProfileService profiles,
    IClock clock,
    WriteGate writeGate) : ICatalogService
{
    public const int SkuMaximum = 64;

    public async Task<Result<Guid>> SaveCategoryAsync(UserSession session, CategoryInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var auth = await identity.AuthorizeAsync(session, Capabilities.ProductsManage, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<Guid>();
        }

        var invalid = TextRules.ValidateRequired(input.Name, "Category name", TextRules.NameMaximum);
        if (invalid is not null)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, invalid);
        }

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var normalized = Normalize(input.Name);
        if (await db.Categories.AnyAsync(c => c.NormalizedName == normalized && c.Id != input.Id, cancellationToken).ConfigureAwait(false))
        {
            return Result<Guid>.Fail(ResultStatus.Conflict, "A category with that name already exists.");
        }

        var now = clock.UtcNowMilliseconds;
        Category category;
        if (input.Id is null)
        {
            category = new Category { Id = Guid.NewGuid(), Name = input.Name.Trim(), NormalizedName = normalized, Description = input.Description, IsActive = input.IsActive };
            db.Categories.Add(category);
        }
        else
        {
            var existing = await db.Categories.SingleOrDefaultAsync(c => c.Id == input.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Result<Guid>.Fail(ResultStatus.NotFound, "Category not found.");
            }

            category = existing;
            if (category.Version != input.ExpectedVersion)
            {
                return Result<Guid>.Fail(ResultStatus.Conflict, "This category was changed by someone else. Reload and try again.");
            }

            category.Name = input.Name.Trim();
            category.NormalizedName = normalized;
            category.Description = input.Description;
            category.IsActive = input.IsActive;
            category.Version++;
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Catalog.CategorySaved", "Category", category.Id.ToString(), null,
            new { name = category.Name, active = category.IsActive });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result<Guid>.Ok(category.Id);
    }

    public async Task<Result<Guid>> SaveProductAsync(UserSession session, ProductInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var auth = await identity.AuthorizeAsync(session, Capabilities.ProductsManage, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<Guid>();
        }

        var invalid = TextRules.ValidateRequired(input.Name, "Product name", TextRules.NameMaximum)
            ?? TextRules.ValidateRequired(input.Sku, "SKU", SkuMaximum)
            ?? (input.UnitPriceMinor is <= 0 or > FinancialLimits.MaxDocumentMinor ? "Price must be positive." : null)
            ?? (input.UnitStepQ is not (FinancialLimits.WholeUnitStepQ or FinancialLimits.FractionalStepQ) ? "Unit step must be whole units or fractional." : null)
            ?? (input.TaxKey is { Length: > 64 } ? "Tax key is too long." : null);
        if (invalid is not null)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, invalid);
        }

        // Products are priced in the terminal currency and must map to a configured tax entry.
        var profile = await profiles.GetEffectiveCalculationProfileAsync(clock.UtcNowMilliseconds, cancellationToken).ConfigureAwait(false);
        if (!profile.IsSuccess)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, "Configure a calculation profile before adding products.");
        }

        // No taxation (POS-DEC-009): a product saved without a tax key gets the profile's no-tax entry.
        var taxKey = string.IsNullOrWhiteSpace(input.TaxKey)
            ? profile.Value!.TaxRules.Where(r => r.Value.Treatment == TreatmentCode.NotTaxed).Select(r => r.Key).Order(StringComparer.Ordinal).FirstOrDefault()
            : input.TaxKey.Trim();
        if (taxKey is null)
        {
            return Result<Guid>.Fail(ResultStatus.Validation, "The calculation profile in effect still has tax entries. Switch the POS to no tax in Settings → Calendar and tax first.");
        }

        if (!profile.Value!.TaxRules.ContainsKey(taxKey))
        {
            return Result<Guid>.Fail(ResultStatus.Validation, $"Tax key '{taxKey}' is not configured in the current calculation profile.");
        }

        var currency = profile.Value.Profile.Currency;

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.Categories.AnyAsync(c => c.Id == input.CategoryId, cancellationToken).ConfigureAwait(false))
        {
            return Result<Guid>.Fail(ResultStatus.Validation, "Choose an existing category.");
        }

        var sku = Normalize(input.Sku);
        if (await db.Products.AnyAsync(p => p.NormalizedSku == sku && p.Id != input.Id, cancellationToken).ConfigureAwait(false))
        {
            return Result<Guid>.Fail(ResultStatus.Conflict, "That SKU is already used by another product.");
        }

        var now = clock.UtcNowMilliseconds;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Product product;
        object details;
        if (input.Id is null)
        {
            product = new Product
            {
                Id = Guid.NewGuid(),
                CategoryId = input.CategoryId,
                Sku = input.Sku.Trim(),
                NormalizedSku = sku,
                Name = input.Name.Trim(),
                Description = input.Description,
                UnitPriceM = input.UnitPriceMinor,
                CurrencyCode = currency.Code,
                CurrencyExponent = currency.Exponent,
                UnitStepQ = input.UnitStepQ,
                TrackStock = input.TrackStock,
                TaxKey = taxKey,
                IsActive = input.IsActive,
                UpdatedUtc = now,
            };
            db.Products.Add(product);
            db.Inventory.Add(new InventoryBalance { ProductId = product.Id, QuantityQ = 0, ReorderQ = 0, UpdatedUtc = now });
            details = new { sku = product.Sku, priceAfter = product.UnitPriceM, active = product.IsActive };
        }
        else
        {
            var existing = await db.Products.SingleOrDefaultAsync(p => p.Id == input.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Result<Guid>.Fail(ResultStatus.NotFound, "Product not found.");
            }

            product = existing;
            if (product.Version != input.ExpectedVersion)
            {
                return Result<Guid>.Fail(ResultStatus.Conflict, "This product was changed by someone else. Reload and try again.");
            }

            details = new
            {
                sku = input.Sku.Trim(),
                priceBefore = product.UnitPriceM,
                priceAfter = input.UnitPriceMinor,
                activeBefore = product.IsActive,
                activeAfter = input.IsActive,
                trackStockBefore = product.TrackStock,
                trackStockAfter = input.TrackStock,
                taxKeyBefore = product.TaxKey,
                taxKeyAfter = taxKey,
            };
            product.CategoryId = input.CategoryId;
            product.Sku = input.Sku.Trim();
            product.NormalizedSku = sku;
            product.Name = input.Name.Trim();
            product.Description = input.Description;
            product.UnitPriceM = input.UnitPriceMinor;
            product.UnitStepQ = input.UnitStepQ;
            product.TrackStock = input.TrackStock;
            product.TaxKey = taxKey;
            product.IsActive = input.IsActive;
            product.UpdatedUtc = now;
            product.Version++;
        }

        AuditRecorder.Record(db, now, session.UserId, session.DisplayName, "Catalog.ProductSaved", "Product", product.Id.ToString(), null, details);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<Guid>.Ok(product.Id);
    }

    public async Task<Result<long>> AdjustStockAsync(UserSession session, StockAdjustmentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var auth = await identity.AuthorizeAsync(session, Capabilities.InventoryAdjust, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<long>();
        }

        if (input.AllowNegative)
        {
            var overrideAuth = await identity.AuthorizeAsync(session, Capabilities.InventoryNegativeOverride, true, cancellationToken).ConfigureAwait(false);
            if (!overrideAuth.IsSuccess)
            {
                return overrideAuth.As<long>();
            }
        }

        var invalid = TextRules.ValidateRequired(input.Reason, "Reason", TextRules.ReasonMaximum);
        if (invalid is not null)
        {
            return Result<long>.Fail(ResultStatus.Validation, invalid);
        }

        var type = input.IsOpening ? InventoryMovementType.Opening : InventoryMovementType.Adjustment;
        var movementKey = $"{input.RequestId:D}:{input.ProductId:D}:{type}";

        using var gate = await writeGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var repeated = await db.InventoryMovements.AsNoTracking().SingleOrDefaultAsync(m => m.MovementKey == movementKey, cancellationToken).ConfigureAwait(false);
        if (repeated is not null)
        {
            return repeated.DeltaQ == input.DeltaQ
                ? Result<long>.Ok(repeated.BalanceAfterQ)
                : Result<long>.Fail(ResultStatus.Conflict, "This request id was already used for a different stock change.");
        }

        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == input.ProductId, cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            return Result<long>.Fail(ResultStatus.NotFound, "Product not found.");
        }

        if (!product.TrackStock)
        {
            return Result<long>.Fail(ResultStatus.Validation, "This product does not track stock.");
        }

        if (input.DeltaQ == 0 || Math.Abs(input.DeltaQ) > FinancialLimits.MaxQuantityQ || input.DeltaQ % product.UnitStepQ != 0)
        {
            return Result<long>.Fail(ResultStatus.Validation, "The quantity change must be non-zero and a whole multiple of the product's unit step.");
        }

        if (input.IsOpening && await db.InventoryMovements.AnyAsync(m => m.ProductId == product.Id, cancellationToken).ConfigureAwait(false))
        {
            return Result<long>.Fail(ResultStatus.Conflict, "Opening stock can only be recorded before any other movement; use an adjustment.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var balance = await db.Inventory.SingleAsync(i => i.ProductId == product.Id, cancellationToken).ConfigureAwait(false);
        var after = checked(balance.QuantityQ + input.DeltaQ);
        if (after < 0 && !input.AllowNegative)
        {
            return Result<long>.Fail(ResultStatus.InsufficientStock, "This change would make stock negative.");
        }

        var now = clock.UtcNowMilliseconds;
        balance.QuantityQ = after;
        balance.UpdatedUtc = now;
        balance.Version++;
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            DeltaQ = input.DeltaQ,
            Type = type,
            Reason = input.Reason.Trim(),
            ActorUserId = session.UserId,
            ApprovedByUserId = input.AllowNegative ? session.UserId : null,
            RequestId = input.RequestId,
            MovementKey = movementKey,
            CreatedUtc = now,
            BalanceAfterQ = after,
        });
        AuditRecorder.Record(db, now, session.UserId, session.DisplayName,
            input.AllowNegative && after < 0 ? "Inventory.NegativeOverride" : $"Inventory.{type}", "Product", product.Id.ToString(), input.Reason,
            new { deltaQ = input.DeltaQ, balanceAfterQ = after }, input.RequestId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<long>.Ok(after);
    }

    public async Task<Result<IReadOnlyList<CategoryView>>> ListCategoriesAsync(UserSession session, bool includeInactive, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.PosSell, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            auth = await identity.AuthorizeAsync(session, Capabilities.ProductsManage, false, cancellationToken).ConfigureAwait(false);
            if (!auth.IsSuccess)
            {
                return auth.As<IReadOnlyList<CategoryView>>();
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CategoryView> rows = await db.Categories.AsNoTracking()
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryView(c.Id, c.Name, c.Description, c.IsActive, c.Version))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<CategoryView>>.Ok(rows);
    }

    public async Task<Result<IReadOnlyList<ProductView>>> SearchProductsAsync(UserSession session, string? text, bool includeInactive, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.PosSell, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            auth = await identity.AuthorizeAsync(session, Capabilities.ProductsManage, false, cancellationToken).ConfigureAwait(false);
            if (!auth.IsSuccess)
            {
                return auth.As<IReadOnlyList<ProductView>>();
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = from p in db.Products.AsNoTracking()
                    join i in db.Inventory.AsNoTracking() on p.Id equals i.ProductId
                    select new { p, i.QuantityQ };
        if (!includeInactive)
        {
            query = query.Where(x => x.p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var term = text.Trim().ToUpperInvariant();
            query = query.Where(x => x.p.NormalizedSku.Contains(term) || x.p.Name.ToUpper().Contains(term));
        }

        IReadOnlyList<ProductView> views = (await query.OrderBy(x => x.p.Name).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(x => new ProductView(x.p.Id, x.p.CategoryId, x.p.Sku, x.p.Name, x.p.UnitPriceM, x.p.CurrencyCode, x.p.CurrencyExponent,
                x.p.UnitStepQ, x.p.TrackStock, x.p.TaxKey, x.p.IsActive, x.p.Version, x.QuantityQ))
            .ToArray();
        return Result<IReadOnlyList<ProductView>>.Ok(views);
    }

    public async Task<Result<IReadOnlyList<StockMovementView>>> GetMovementsAsync(UserSession session, Guid productId, CancellationToken cancellationToken = default)
    {
        var auth = await identity.AuthorizeAsync(session, Capabilities.InventoryAdjust, false, cancellationToken).ConfigureAwait(false);
        if (!auth.IsSuccess)
        {
            return auth.As<IReadOnlyList<StockMovementView>>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var movements = await db.InventoryMovements.AsNoTracking()
            .Where(m => m.ProductId == productId)
            .OrderBy(m => m.CreatedUtc).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StockMovementView> rows = movements
            .Select(m => new StockMovementView(m.Id, m.Type.ToString(), m.DeltaQ, m.BalanceAfterQ, m.Reason, m.ActorUserId, m.CreatedUtc))
            .ToArray();
        return Result<IReadOnlyList<StockMovementView>>.Ok(rows);
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
