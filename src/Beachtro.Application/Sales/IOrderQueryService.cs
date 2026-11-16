using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Sales;

public sealed record OrderSummary(
    Guid OrderId,
    string TransactionNumber,
    string BusinessDate,
    long CompletedUtc,
    string CashierName,
    OrderType OrderType,
    PaymentMethod Method,
    long TotalMinor,
    long CorrectedTotalMinor,
    int CurrencyExponent);

/// <summary>A sold line with how much has already been returned, for refund selection.</summary>
public sealed record OrderLineDetail(
    Guid OrderItemId,
    int LineNo,
    string Sku,
    string Name,
    long QuantityQ,
    long UnitStepQ,
    long UnitPriceMinor,
    long TotalMinor,
    bool TrackStock,
    long ReturnedQ)
{
    public long RemainingQ => QuantityQ - ReturnedQ;
}

public sealed record CorrectionSummary(Guid CorrectionId, string CorrectionNumber, CorrectionKind Kind, long TotalMinor, long CreatedUtc, string Reason);

public sealed record OrderDetail(
    OrderSummary Summary,
    IReadOnlyList<OrderLineDetail> Lines,
    IReadOnlyList<CorrectionSummary> Corrections,
    string? PaymentReference,
    long? TenderedMinor,
    long? ChangeMinor);

/// <summary>
/// Transaction history (FR-017). Scope is enforced in the service: orders.view.all sees every sale,
/// orders.view.own only the caller's own; another cashier's sale is reported as not found.
/// </summary>
public interface IOrderQueryService
{
    Task<Result<IReadOnlyList<OrderSummary>>> ListAsync(UserSession session, string? businessDate, string? search, int take = 200, CancellationToken cancellationToken = default);

    Task<Result<OrderDetail>> GetAsync(UserSession session, Guid orderId, CancellationToken cancellationToken = default);
}
