namespace Beachtro.Domain.Financial;

/// <summary>
/// Selected safety bounds (database design §Physical conventions). They are engineering limits,
/// not business sales-volume promises; revise prospectively with tests.
/// </summary>
public static class FinancialLimits
{
    /// <summary>Quantity scale: Q is thousandths of one product unit.</summary>
    public const long QuantityScale = 1000;

    /// <summary>Whole-unit step (default) and the only fractional step supported initially.</summary>
    public const long WholeUnitStepQ = 1000;
    public const long FractionalStepQ = 1;

    /// <summary>1,000,000 units per product line.</summary>
    public const long MaxQuantityQ = 1_000_000 * QuantityScale;

    /// <summary>Largest money amount on any single document or line, in minor units.</summary>
    public const long MaxDocumentMinor = 100_000_000_000;

    public const int MaxLinesPerCart = 100;

    /// <summary>Rates are integer parts per million; 1,000,000 = 100%.</summary>
    public const int RateScalePpm = 1_000_000;

    public const int MaxCurrencyExponent = 4;
}
