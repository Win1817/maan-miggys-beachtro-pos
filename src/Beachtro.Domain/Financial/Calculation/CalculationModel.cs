namespace Beachtro.Domain.Financial.Calculation;

/// <summary>Whether a configured price already contains tax (inclusive) or is a tax-exclusive base.</summary>
public enum PriceMode
{
    Inclusive,
    Exclusive,
}

/// <summary>
/// Explicit tax treatment. Unknown is never equivalent to Exempt/ZeroRated; status is never
/// inferred from a zero rate (finance design §Money and policy, D-01).
/// TestNoTax exists only for synthetic Test-profile fixtures.
/// </summary>
public enum TreatmentCode
{
    Unknown,
    Taxable,
    Exempt,
    ZeroRated,
    TestNoTax,
}

/// <summary>Draft profiles cannot calculate; Test is synthetic-only; Approved needs evidence (IB-06).</summary>
public enum ProfileApprovalState
{
    Draft,
    Test,
    Approved,
}

/// <summary>
/// The calculation profile revision applied to a quote. The kernel checks only what it needs:
/// approval state, currency and strategy identity. Effective dating and evidence live in IB-06.
/// </summary>
public sealed record CalculationProfile(
    Guid Id,
    int Revision,
    ProfileApprovalState ApprovalState,
    Currency Currency,
    string StrategyKey);

/// <summary>One ad-valorem tax component, rate in parts per million.</summary>
public sealed record TaxComponentRule(string Key, int RatePpm);

/// <summary>Resolved tax treatment for one line.</summary>
public sealed record LineTaxRule(PriceMode PriceMode, TreatmentCode Treatment, IReadOnlyList<TaxComponentRule> Components)
{
    public static LineTaxRule TestNoTax { get; } = new(PriceMode.Inclusive, TreatmentCode.TestNoTax, []);

    public static LineTaxRule Taxable(PriceMode mode, string key, int ratePpm) =>
        new(mode, TreatmentCode.Taxable, [new TaxComponentRule(key, ratePpm)]);
}

/// <summary>A cart line as quoted: stable product identity, unit price in minor units and quantity Q.</summary>
public sealed record CartLine(
    int LineNo,
    Guid ProductId,
    long UnitPriceMinor,
    long QuantityQ,
    long UnitStepQ,
    Currency Currency,
    bool DiscountEligible,
    LineTaxRule Tax);

public enum OrderDiscountKind
{
    None,
    FixedMinor,
    PercentPpm,
}

/// <summary>At most one generic, non-stacking order discount (B-01 provisional policy).</summary>
public sealed record OrderDiscount(OrderDiscountKind Kind, long Value)
{
    public static OrderDiscount None { get; } = new(OrderDiscountKind.None, 0);

    public static OrderDiscount Fixed(long minor) => new(OrderDiscountKind.FixedMinor, minor);

    public static OrderDiscount Percent(int ppm) => new(OrderDiscountKind.PercentPpm, ppm);
}

public sealed record TaxComponentAmount(string Key, int RatePpm, long TaxableBaseMinor, long TaxMinor);

/// <summary>
/// Rounded, persisted components for one line. Identities: NetBeforeDiscount = Net + DiscountExTax;
/// Total = Net + Tax; DiscountBasis is the discount on the configured price basis.
/// </summary>
public sealed record LineCalculation(
    int LineNo,
    Guid ProductId,
    long UnitPriceMinor,
    long QuantityQ,
    long UnitStepQ,
    PriceMode PriceMode,
    TreatmentCode Treatment,
    long PriceBasisMinor,
    long DiscountBasisMinor,
    long NetBeforeDiscountMinor,
    long DiscountExTaxMinor,
    long NetMinor,
    long TaxMinor,
    long TotalMinor,
    IReadOnlyList<TaxComponentAmount> TaxComponents);

/// <summary>Order totals are sums of rounded line components, never a re-rounding of a raw total.</summary>
public sealed record OrderCalculation(
    Currency Currency,
    Guid ProfileId,
    int ProfileRevision,
    string StrategyKey,
    IReadOnlyList<LineCalculation> Lines,
    long PriceBasisSubtotalMinor,
    long DiscountBasisMinor,
    long NetBeforeDiscountMinor,
    long DiscountExTaxMinor,
    long NetMinor,
    long TaxMinor,
    long TotalMinor);
