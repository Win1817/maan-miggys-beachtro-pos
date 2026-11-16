namespace Beachtro.Domain.Financial.Corrections;

/// <summary>Immutable original line components that a refund reverses. Never recalculated under today's profile.</summary>
public sealed record OriginalLineComponents(
    long OriginalQuantityQ,
    long UnitStepQ,
    long NetMinor,
    long DiscountExTaxMinor,
    long DiscountBasisMinor,
    IReadOnlyList<OriginalTaxComponent> TaxComponents);

public sealed record OriginalTaxComponent(string Key, long TaxMinor);

public sealed record CorrectionTaxAmount(string Key, long TaxMinor);

/// <summary>Positive reversal magnitudes for one correction of one line.</summary>
public sealed record CorrectionLineAmounts(
    long QuantityQ,
    long CumulativeReturnedQ,
    long NetMinor,
    long DiscountExTaxMinor,
    long DiscountBasisMinor,
    IReadOnlyList<CorrectionTaxAmount> TaxComponents,
    long TaxMinor,
    long TotalMinor);

/// <summary>
/// Cumulative-component refund allocation (finance design §Corrections). For each original
/// component C over original quantity Q: A(q) = floor(C·q/Q) for q &lt; Q and A(Q) = C. A correction
/// from q0 to q1 reverses A(q1) − A(q0). Monotone, capped, and returns every minor unit on full return.
/// </summary>
public static class CorrectionAllocator
{
    public static CorrectionLineAmounts Allocate(OriginalLineComponents original, long alreadyReturnedQ, long returnQ)
    {
        ArgumentNullException.ThrowIfNull(original);
        var q = original.OriginalQuantityQ;

        if (q <= 0 || original.UnitStepQ <= 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.InvalidCorrectionQuantity, "Original quantity and step must be positive.");
        }

        if (original.NetMinor < 0 || original.DiscountExTaxMinor < 0 || original.DiscountBasisMinor < 0 || original.TaxComponents.Any(t => t.TaxMinor < 0))
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, "Original components must be non-negative.");
        }

        if (returnQ <= 0 || returnQ % original.UnitStepQ != 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.InvalidCorrectionQuantity, $"Return quantity {returnQ}Q must be positive and a multiple of step {original.UnitStepQ}Q.");
        }

        if (alreadyReturnedQ < 0 || alreadyReturnedQ % original.UnitStepQ != 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.InvalidCorrectionQuantity, $"Previously returned quantity {alreadyReturnedQ}Q is invalid.");
        }

        var cumulative = checked(alreadyReturnedQ + returnQ);
        if (cumulative > q)
        {
            throw new FinancialRuleException(FinancialErrorCode.OverRefund, $"Returning {returnQ}Q after {alreadyReturnedQ}Q exceeds the original {q}Q.");
        }

        long Delta(long component) => Cumulative(component, cumulative, q) - Cumulative(component, alreadyReturnedQ, q);

        var taxes = original.TaxComponents.Select(t => new CorrectionTaxAmount(t.Key, Delta(t.TaxMinor))).ToArray();
        var net = Delta(original.NetMinor);
        var tax = ExactMath.CheckedSum(taxes.Select(t => t.TaxMinor));

        return new CorrectionLineAmounts(
            returnQ,
            cumulative,
            net,
            Delta(original.DiscountExTaxMinor),
            Delta(original.DiscountBasisMinor),
            taxes,
            tax,
            checked(net + tax));
    }

    private static long Cumulative(long component, long returnedQ, long originalQ) =>
        returnedQ == originalQ ? component : ExactMath.FloorNonNegative((Int128)component * returnedQ, originalQ);
}
