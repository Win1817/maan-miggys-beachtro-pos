using Beachtro.Application.Common;
using Beachtro.Application.Security;

namespace Beachtro.Application.Catalog;

/// <summary>Create (Id null) or update a category. Updates must name the version they edited.</summary>
public sealed record CategoryInput(Guid? Id, string Name, string? Description, bool IsActive, int? ExpectedVersion);

/// <summary>Finished product. Price in minor units of the terminal currency; UnitStepQ 1000 = whole units.</summary>
public sealed record ProductInput(
    Guid? Id,
    Guid CategoryId,
    string Sku,
    string Name,
    string? Description,
    long UnitPriceMinor,
    long UnitStepQ,
    bool TrackStock,
    string TaxKey,
    bool IsActive,
    int? ExpectedVersion);

/// <summary>
/// A stock change with a stable RequestId (retry-safe). Opening is allowed once per product, before
/// any other movement. AllowNegative needs inventory.negative.override and is audited.
/// </summary>
public sealed record StockAdjustmentInput(Guid RequestId, Guid ProductId, long DeltaQ, string Reason, bool IsOpening, bool AllowNegative = false);

public sealed record ProductView(
    Guid Id,
    Guid CategoryId,
    string Sku,
    string Name,
    long UnitPriceMinor,
    string CurrencyCode,
    int CurrencyExponent,
    long UnitStepQ,
    bool TrackStock,
    string TaxKey,
    bool IsActive,
    int Version,
    long OnHandQ);

public sealed record CategoryView(Guid Id, string Name, string? Description, bool IsActive, int Version);

public sealed record StockMovementView(Guid Id, string Type, long DeltaQ, long BalanceAfterQ, string? Reason, Guid ActorUserId, long CreatedUtc);

/// <summary>Catalog and finished-product stock (FR-004–008; IB-07).</summary>
public interface ICatalogService
{
    Task<Result<Guid>> SaveCategoryAsync(UserSession session, CategoryInput input, CancellationToken cancellationToken = default);

    Task<Result<Guid>> SaveProductAsync(UserSession session, ProductInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns the balance after the change. Repeating a RequestId returns the original result.</summary>
    Task<Result<long>> AdjustStockAsync(UserSession session, StockAdjustmentInput input, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<CategoryView>>> ListCategoriesAsync(UserSession session, bool includeInactive, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ProductView>>> SearchProductsAsync(UserSession session, string? text, bool includeInactive, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<StockMovementView>>> GetMovementsAsync(UserSession session, Guid productId, CancellationToken cancellationToken = default);
}
