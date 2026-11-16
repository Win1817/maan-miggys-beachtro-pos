namespace Beachtro.Domain.Financial;

/// <summary>Typed reasons a financial calculation is rejected (F-08, F-12). Rejection has no side effects.</summary>
public enum FinancialErrorCode
{
    InvalidCurrency,
    MixedCurrency,
    EmptyCart,
    TooManyLines,
    InvalidLineNumber,
    DuplicateLineNumber,
    InvalidPrice,
    InvalidQuantity,
    InvalidUnitStep,
    QuantityNotAlignedToStep,
    InvalidRate,
    InvalidDiscount,
    DiscountExceedsEligibleAmount,
    UnknownTaxTreatment,
    InvalidTaxPolicy,
    UnsupportedPolicy,
    ProfileNotUsable,
    AmountOutOfRange,
    NonPositiveTotal,
    InsufficientTender,
    InvalidCorrectionQuantity,
    OverRefund,
}

public sealed class FinancialRuleException : Exception
{
    public FinancialRuleException(FinancialErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public FinancialErrorCode Code { get; }
}
