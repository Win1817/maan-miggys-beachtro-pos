using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;

namespace Beachtro.Application.Configuration;

/// <summary>How one sample line splits under a tax entry, or why it cannot be calculated.</summary>
public sealed record PreviewLine(
    string TaxKey,
    TreatmentCode Treatment,
    PriceMode PriceMode,
    int RatePpm,
    long PriceMinor,
    long NetMinor,
    long TaxMinor,
    long TotalMinor,
    string? Problem);

/// <summary>
/// Pure, unsaved preview of a calculation profile draft (POS-DEC-008): one line of quantity 1 at the
/// sample price per tax entry, through the same calculator checkout uses.
/// </summary>
public static class ProfilePreview
{
    public static IReadOnlyList<PreviewLine> Sample(CalculationProfileDraft draft, long samplePriceMinor)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // The approval state only gates whether a profile may be used for real sales; the arithmetic
        // is the same, so a Draft is previewed as Test.
        var state = draft.ApprovalState == ProfileApprovalState.Draft ? ProfileApprovalState.Test : draft.ApprovalState;
        var profile = new CalculationProfile(Guid.Empty, 0, state, draft.Currency, OrderCalculator.StrategyKey);
        var lines = new List<PreviewLine>(draft.Taxes.Count);
        foreach (var t in draft.Taxes)
        {
            var rule = t.Treatment == TreatmentCode.Taxable
                ? LineTaxRule.Taxable(t.PriceMode, t.TaxKey, t.RatePpm)
                : new LineTaxRule(t.PriceMode, t.Treatment, []);
            try
            {
                var line = new CartLine(1, Guid.Empty, samplePriceMinor, FinancialLimits.QuantityScale, FinancialLimits.QuantityScale, draft.Currency, false, rule);
                var calc = OrderCalculator.Calculate(profile, [line], OrderDiscount.None);
                lines.Add(new PreviewLine(t.TaxKey, t.Treatment, t.PriceMode, t.RatePpm, calc.PriceBasisSubtotalMinor, calc.NetMinor, calc.TaxMinor, calc.TotalMinor, null));
            }
            catch (FinancialRuleException ex)
            {
                lines.Add(new PreviewLine(t.TaxKey, t.Treatment, t.PriceMode, t.RatePpm, samplePriceMinor, 0, 0, 0, ex.Message));
            }
        }

        return lines;
    }
}
