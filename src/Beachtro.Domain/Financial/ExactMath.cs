namespace Beachtro.Domain.Financial;

/// <summary>
/// Exact integer rational arithmetic. Every rounding in the kernel is an explicit division of
/// 128-bit integers, so results equal the design's RoundM(x) (midpoint away from zero) with no
/// intermediate precision limit and no binary floating point (BRD §8).
/// </summary>
public static class ExactMath
{
    /// <summary>Round numerator/denominator to the nearest integer, halves away from zero.</summary>
    public static long RoundHalfAwayFromZero(Int128 numerator, Int128 denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be positive.");
        }

        var quotient = numerator / denominator; // truncates toward zero
        var remainder = numerator % denominator; // same sign as numerator
        var twiceAbsRemainder = Int128.Abs(remainder) * 2;
        if (twiceAbsRemainder >= denominator)
        {
            quotient += numerator < 0 ? -1 : 1;
        }

        return ToLong(quotient);
    }

    /// <summary>Floor of numerator/denominator for non-negative operands.</summary>
    public static long FloorNonNegative(Int128 numerator, Int128 denominator)
    {
        if (numerator < 0 || denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Operands must be non-negative with a positive denominator.");
        }

        return ToLong(numerator / denominator);
    }

    /// <summary>Overflow-checked sum; never wraps (F-12).</summary>
    public static long CheckedSum(IEnumerable<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        long total = 0;
        try
        {
            foreach (var v in values)
            {
                total = checked(total + v);
            }
        }
        catch (OverflowException ex)
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, "Sum exceeds the 64-bit integer range: " + ex.Message);
        }

        return total;
    }

    /// <summary>Rejects amounts outside 0..MaxDocumentMinor.</summary>
    public static long RequireDocumentAmount(long value, string what)
    {
        if (value is < 0 or > FinancialLimits.MaxDocumentMinor)
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, $"{what} {value} is outside 0..{FinancialLimits.MaxDocumentMinor}.");
        }

        return value;
    }

    private static long ToLong(Int128 value)
    {
        if (value > long.MaxValue || value < long.MinValue)
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, "Value exceeds the 64-bit integer range.");
        }

        return (long)value;
    }
}
