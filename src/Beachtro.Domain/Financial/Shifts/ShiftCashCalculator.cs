namespace Beachtro.Domain.Financial.Shifts;

/// <summary>
/// Expected drawer cash (finance design §Reporting dictionary): opening + cash sale applied amounts
/// + cash-in − cash-out − cash refunds. Excludes GCash, and uses applied amounts, never tendered
/// cash, so change is not double counted (F-10).
/// </summary>
public static class ShiftCashCalculator
{
    public static long Expected(long openingMinor, long cashSalesAppliedMinor, long cashInMinor, long cashOutMinor, long cashRefundsMinor)
    {
        foreach (var (value, name) in new[]
                 {
                     (openingMinor, "Opening cash"), (cashSalesAppliedMinor, "Cash sales"), (cashInMinor, "Cash-in"),
                     (cashOutMinor, "Cash-out"), (cashRefundsMinor, "Cash refunds"),
                 })
        {
            if (value < 0)
            {
                throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, $"{name} cannot be negative.");
            }
        }

        try
        {
            return checked(openingMinor + cashSalesAppliedMinor + cashInMinor - cashOutMinor - cashRefundsMinor);
        }
        catch (OverflowException)
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, "Expected cash exceeds the 64-bit integer range.");
        }
    }

    /// <summary>Counted minus expected; negative means the drawer is short.</summary>
    public static long Variance(long countedMinor, long expectedMinor)
    {
        if (countedMinor < 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, "Counted cash cannot be negative.");
        }

        return checked(countedMinor - expectedMinor);
    }
}
