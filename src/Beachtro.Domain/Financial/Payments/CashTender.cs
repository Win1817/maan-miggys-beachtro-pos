namespace Beachtro.Domain.Financial.Payments;

/// <summary>Applied amount is always the order total; tendered and change are separate facts (F-01).</summary>
public sealed record CashSettlement(long AppliedMinor, long TenderedMinor, long ChangeMinor);

public static class CashTender
{
    /// <summary>
    /// One tender per sale; cash must cover the full total; no credit; positive totals only
    /// (B-02 provisional policy, D-07).
    /// </summary>
    public static CashSettlement Settle(long totalMinor, long tenderedMinor)
    {
        if (totalMinor <= 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.NonPositiveTotal, "Only positive-total sales can be completed under the current policy.");
        }

        ExactMath.RequireDocumentAmount(totalMinor, "Total");
        ExactMath.RequireDocumentAmount(tenderedMinor, "Tendered amount");

        if (tenderedMinor < totalMinor)
        {
            throw new FinancialRuleException(FinancialErrorCode.InsufficientTender, $"Tendered {tenderedMinor} is less than total {totalMinor}.");
        }

        return new CashSettlement(totalMinor, tenderedMinor, tenderedMinor - totalMinor);
    }
}
