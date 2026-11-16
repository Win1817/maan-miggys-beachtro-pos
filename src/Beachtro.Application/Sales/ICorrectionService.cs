using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Sales;

public sealed record CorrectionLineInput(Guid OrderItemId, long QuantityQ, StockDisposition Disposition);

/// <summary>
/// Refund: any positive quantities within what remains. Void: every line at full original quantity,
/// only when no correction exists yet (D-04 provisional policy).
/// </summary>
public sealed record CorrectionInput(Guid OriginalOrderId, CorrectionKind Kind, IReadOnlyList<CorrectionLineInput> Lines, string Reason);

/// <summary>Evidence that money went back by the original tender; no transfer is ever automated.</summary>
public abstract record RefundEvidence;

public sealed record CashPaidOut : RefundEvidence;

public sealed record GCashRefunded(string? Reference) : RefundEvidence;

public sealed record CorrectionPreview(Guid RequestId, long NetMinor, long TaxMinor, long TotalMinor, PaymentMethod RefundMethod);

public sealed record CorrectionReceipt(Guid CorrectionId, string CorrectionNumber, CorrectionKind Kind, long NetMinor, long TaxMinor, long TotalMinor, string BusinessDate);

/// <summary>Linked, immutable refunds and voids (FR-018; IB-11). Originals are never edited.</summary>
public interface ICorrectionService
{
    Task<Result<CorrectionPreview>> PrepareAsync(UserSession session, Guid requestId, CorrectionInput input, CancellationToken cancellationToken = default);

    Task<Result<CorrectionReceipt>> CompleteAsync(UserSession session, Guid requestId, RefundEvidence evidence, CancellationToken cancellationToken = default);

    Task<Result<Unit>> AbandonAsync(UserSession session, Guid requestId, string reason, CancellationToken cancellationToken = default);
}
