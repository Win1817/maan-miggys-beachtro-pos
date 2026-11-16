using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;

namespace Beachtro.Infrastructure.Persistence.Entities;

/// <summary>Recoverable unpaid cart. Not financial history and not a stock reservation.</summary>
public sealed class Draft
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public required string CartJson { get; set; }
    public long UpdatedUtc { get; set; }
    public int Version { get; set; } = 1;
}

/// <summary>Durable checkout identity; Id is the idempotency key.</summary>
public sealed class CheckoutRequest
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string QuoteJson { get; set; }
    public required string QuoteSha256 { get; set; }
    public RequestState State { get; set; }
    public long PreparedUtc { get; set; }
    public long? ResolvedUtc { get; set; }
    public Guid? OrderId { get; set; }
    public string? ExternalEvidenceJson { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class NumberSeries
{
    public required string SeriesKey { get; set; }
    public required string BusinessDate { get; set; }
    public required string Prefix { get; set; }
    public int LastReservedSequence { get; set; }
    public NumberSeriesStatus Status { get; set; }
    public long? LastReconciledUtc { get; set; }
    public string? EvidenceRef { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid CheckoutRequestId { get; set; }
    public required string TransactionNumber { get; set; }
    public required string NumberSeriesKey { get; set; }
    public int Sequence { get; set; }
    public OrderType OrderType { get; set; }
    public OrderState State { get; set; }
    public Guid CashierUserId { get; set; }
    public Guid? ShiftId { get; set; }
    public long CreatedUtc { get; set; }
    public long CompletedUtc { get; set; }
    public required string BusinessDate { get; set; }
    public required string TimezoneId { get; set; }
    public int CutoffMinute { get; set; }
    public required string CurrencyCode { get; set; }
    public int CurrencyExponent { get; set; }
    public Guid CalculationPolicyId { get; set; }
    public Guid BusinessProfileId { get; set; }
    public long NetM { get; set; }
    public long DiscountExTaxM { get; set; }
    public long TaxM { get; set; }
    public long TotalM { get; set; }
    public long PriceBasisSubtotalM { get; set; }
    public long DiscountBasisM { get; set; }
    public int SnapshotSchemaVersion { get; set; }
    public required string SnapshotJson { get; set; }
    public required string SnapshotSha256 { get; set; }
    public string? LegalInvoiceNumber { get; set; }
}

public sealed class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public required string SkuSnapshot { get; set; }
    public required string NameSnapshot { get; set; }
    public long UnitPriceM { get; set; }
    public long QuantityQ { get; set; }
    public long UnitStepQ { get; set; }
    public bool TrackStockSnapshot { get; set; }
    public PriceMode PriceMode { get; set; }
    public TreatmentCode TreatmentCode { get; set; }
    public long PriceBasisM { get; set; }
    public long DiscountBasisM { get; set; }
    public long NetBeforeDiscountM { get; set; }
    public long DiscountExTaxM { get; set; }
    public long NetM { get; set; }
    public long TaxM { get; set; }
    public long TotalM { get; set; }
    public required string AppliedPolicyJson { get; set; }
}

public sealed class ItemTaxComponent
{
    public Guid Id { get; set; }
    public Guid OrderItemId { get; set; }
    public required string ComponentKey { get; set; }
    public int RatePpm { get; set; }
    public long TaxableBaseM { get; set; }
    public long TaxM { get; set; }
    public TreatmentCode TreatmentCode { get; set; }
    public required string RuleVersion { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public PaymentMethod Method { get; set; }
    public long AppliedM { get; set; }
    public long? TenderedM { get; set; }
    public long? ChangeM { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public PaymentVerificationState VerificationState { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public long? VerifiedUtc { get; set; }
    public Guid RecordedByUserId { get; set; }
    public long PaidUtc { get; set; }
}

public sealed class CorrectionRequest
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid OriginalOrderId { get; set; }
    public required string Fingerprint { get; set; }
    public required string IntendedItemsJson { get; set; }
    public string? PaymentEvidenceJson { get; set; }
    public RequestState State { get; set; }
    public long PreparedUtc { get; set; }
    public long? ResolvedUtc { get; set; }
    public Guid? CorrectionId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Correction
{
    public Guid Id { get; set; }
    public Guid OriginalOrderId { get; set; }
    public Guid CorrectionRequestId { get; set; }
    public required string CorrectionNumber { get; set; }
    public CorrectionKind Kind { get; set; }
    public required string Reason { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public long ApprovalUtc { get; set; }
    public long CreatedUtc { get; set; }
    public required string BusinessDate { get; set; }
    public required string TimezoneId { get; set; }
    public Guid? ShiftId { get; set; }
    public long NetM { get; set; }
    public long DiscountExTaxM { get; set; }
    public long TaxM { get; set; }
    public long TotalM { get; set; }
    public required string SnapshotJson { get; set; }
    public required string SnapshotSha256 { get; set; }
}

public sealed class CorrectionItem
{
    public Guid Id { get; set; }
    public Guid CorrectionId { get; set; }
    public Guid OriginalOrderItemId { get; set; }
    public long QuantityQ { get; set; }
    public long CumulativeReturnedQ { get; set; }
    public long NetM { get; set; }
    public long DiscountExTaxM { get; set; }
    public long TaxM { get; set; }
    public long TotalM { get; set; }
    public long DiscountBasisM { get; set; }
    public StockDisposition StockDisposition { get; set; }
    public long StockReturnedQ { get; set; }
}

public sealed class CorrectionTaxComponent
{
    public Guid Id { get; set; }
    public Guid CorrectionItemId { get; set; }
    public Guid OriginalTaxComponentId { get; set; }
    public long TaxM { get; set; }
}

public sealed class RefundPayment
{
    public Guid Id { get; set; }
    public Guid CorrectionId { get; set; }
    public PaymentMethod Method { get; set; }
    public long AmountM { get; set; }
    public PaymentVerificationState VerificationState { get; set; }
    public Guid VerifiedByUserId { get; set; }
    public long VerifiedUtc { get; set; }
    public string? Reference { get; set; }
    public Guid RecordedByUserId { get; set; }
}
