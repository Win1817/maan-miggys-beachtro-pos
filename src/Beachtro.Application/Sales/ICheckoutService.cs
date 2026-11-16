using Beachtro.Application.Common;
using Beachtro.Application.Security;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;

namespace Beachtro.Application.Sales;

public sealed record CartLineInput(Guid ProductId, long QuantityQ);

/// <summary>Editable cart content. Line numbers follow list order.</summary>
public sealed record CartInput(OrderType OrderType, IReadOnlyList<CartLineInput> Lines, OrderDiscount? Discount = null);

public sealed record QuoteLine(int LineNo, Guid ProductId, string Sku, string Name, int ProductVersion, LineCalculation Calculation);

/// <summary>Priced cart under the effective profile. QuoteHash changes whenever any priced fact changes.</summary>
public sealed record CheckoutQuote(
    OrderType OrderType,
    OrderCalculation Calculation,
    IReadOnlyList<QuoteLine> Lines,
    OrderDiscount Discount,
    string CartFingerprint,
    string QuoteHash);

/// <summary>One tender per sale (B-02 provisional).</summary>
public abstract record PaymentInput;

/// <summary>Cash received from the customer; must cover the total.</summary>
public sealed record CashPayment(long TenderedMinor) : PaymentInput;

/// <summary>
/// GCash is manually verified (BRD §8): the operator confirms the transfer arrived. The reference
/// is optional; a repeated reference is a warning, not proof of anything.
/// </summary>
public sealed record GCashPayment(bool ReceiptVerified, string? Reference, string? Note) : PaymentInput;

public sealed record SaleReceipt(
    Guid OrderId,
    Guid RequestId,
    string TransactionNumber,
    string BusinessDate,
    long TotalMinor,
    PaymentMethod Method,
    long? TenderedMinor,
    long? ChangeMinor,
    IReadOnlyList<string> Warnings);

public enum CheckoutState
{
    NotFound,
    Prepared,
    Completed,
    Abandoned,
}

public sealed record CheckoutOutcome(CheckoutState State, SaleReceipt? Sale, long? QuotedTotalMinor);

/// <summary>
/// Quote → prepare (durable request identity) → complete (one atomic local commit) (finance design
/// §Checkout protocol; IB-09/IB-10). The RequestId is the idempotency key across retries and crashes.
/// </summary>
public interface ICheckoutService
{
    Task<Result<CheckoutQuote>> QuoteAsync(UserSession session, CartInput cart, CancellationToken cancellationToken = default);

    /// <summary>Freezes the quote under RequestId before payment is taken. Same id + different cart is a conflict.</summary>
    Task<Result<CheckoutQuote>> PrepareAsync(UserSession session, Guid requestId, CartInput cart, CancellationToken cancellationToken = default);

    /// <summary>Completes once; a repeat returns the original sale. StaleQuote if any priced fact changed since prepare.</summary>
    Task<Result<SaleReceipt>> CompleteAsync(UserSession session, Guid requestId, PaymentInput payment, CancellationToken cancellationToken = default);

    Task<Result<CheckoutOutcome>> GetOutcomeAsync(UserSession session, Guid requestId, CancellationToken cancellationToken = default);

    Task<Result<Unit>> AbandonAsync(UserSession session, Guid requestId, string reason, CancellationToken cancellationToken = default);
}
