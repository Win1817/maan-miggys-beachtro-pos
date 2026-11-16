using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;

namespace Beachtro.Domain.Tests.Financial;

/// <summary>
/// Synthetic helpers. The 12% rate used in tests is an arithmetic input only — not an approved
/// business tax setting (finance design §D).
/// </summary>
internal static class Fixtures
{
    public const int SyntheticRatePpm = 120_000;

    public static CalculationProfile TestProfile { get; } =
        new(Guid.Parse("00000000-0000-4000-8000-000000000001"), 1, ProfileApprovalState.Test, Currency.Php, OrderCalculator.StrategyKey);

    public static CalculationProfile ApprovedProfile { get; } = TestProfile with { ApprovalState = ProfileApprovalState.Approved };

    public static LineTaxRule Inclusive12 => LineTaxRule.Taxable(PriceMode.Inclusive, "SYN", SyntheticRatePpm);
    public static LineTaxRule Exclusive12 => LineTaxRule.Taxable(PriceMode.Exclusive, "SYN", SyntheticRatePpm);

    public static CartLine Line(
        int lineNo,
        long unitPriceMinor,
        long units = 1,
        LineTaxRule? tax = null,
        bool eligible = true,
        long? quantityQ = null,
        long stepQ = FinancialLimits.WholeUnitStepQ,
        Currency? currency = null) =>
        new(
            lineNo,
            ProductId(lineNo),
            unitPriceMinor,
            quantityQ ?? units * FinancialLimits.QuantityScale,
            stepQ,
            currency ?? Currency.Php,
            eligible,
            tax ?? LineTaxRule.TestNoTax);

    /// <summary>Value representation of a line, including its tax components (records compare lists by reference).</summary>
    public static string ValueOf(LineCalculation line) =>
        (line with { TaxComponents = [] }) + " | " + string.Join("; ", line.TaxComponents);

    public static Guid ProductId(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    public static OrderCalculation Calc(OrderDiscount discount, params CartLine[] lines) =>
        OrderCalculator.Calculate(TestProfile, lines, discount);

    public static OrderCalculation Calc(params CartLine[] lines) => Calc(OrderDiscount.None, lines);
}
