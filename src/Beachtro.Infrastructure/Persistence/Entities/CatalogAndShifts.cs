using Beachtro.Domain.Model;

namespace Beachtro.Infrastructure.Persistence.Entities;

public sealed class Category
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Product
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public required string Sku { get; set; }
    public required string NormalizedSku { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public long UnitPriceM { get; set; }
    public required string CurrencyCode { get; set; }
    public int CurrencyExponent { get; set; }
    public long UnitStepQ { get; set; }
    public bool TrackStock { get; set; }

    /// <summary>Tax entry key in the effective calculation profile (POS-DEC-004).</summary>
    public required string TaxKey { get; set; }

    public bool IsActive { get; set; }
    public int Version { get; set; } = 1;
    public long UpdatedUtc { get; set; }
}

/// <summary>Table "Inventory": one balance row per product.</summary>
public sealed class InventoryBalance
{
    public Guid ProductId { get; set; }
    public long QuantityQ { get; set; }
    public long ReorderQ { get; set; }
    public long UpdatedUtc { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class InventoryMovement
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public long DeltaQ { get; set; }
    public InventoryMovementType Type { get; set; }
    public string? Reason { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? OrderItemId { get; set; }
    public Guid? CorrectionItemId { get; set; }
    public Guid RequestId { get; set; }

    /// <summary>Deterministic idempotency key: request, product, type and source line.</summary>
    public required string MovementKey { get; set; }

    public long CreatedUtc { get; set; }
    public long BalanceAfterQ { get; set; }
}

public sealed class CashierShift
{
    public Guid Id { get; set; }
    public Guid CashierUserId { get; set; }
    public long OpenedUtc { get; set; }
    public long? ClosedUtc { get; set; }
    public long OpeningCashM { get; set; }
    public long? CountedCashM { get; set; }
    public long? ExpectedCashM { get; set; }
    public long? VarianceM { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? CloseReason { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class CashMovement
{
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public CashDirection Direction { get; set; }
    public long AmountM { get; set; }
    public required string Reason { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid RequestId { get; set; }
    public long CreatedUtc { get; set; }
}

public sealed class Discount
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DiscountType Type { get; set; }
    public long ValueScaled { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; }
    public int Version { get; set; } = 1;
}
