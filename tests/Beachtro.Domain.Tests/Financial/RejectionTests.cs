using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Financial.Corrections;
using Beachtro.Domain.Financial.Payments;
using static Beachtro.Domain.Tests.Financial.Fixtures;

namespace Beachtro.Domain.Tests.Financial;

/// <summary>F-08 invalid inputs and F-12 bounds/overflow: typed rejection, never wrapping or approximating.</summary>
public sealed class RejectionTests
{
    private static FinancialErrorCode Rejects(Action action) => Assert.Throws<FinancialRuleException>(action).Code;

    public static TheoryData<string, FinancialErrorCode> InvalidCarts => new()
    {
        { "zero price", FinancialErrorCode.InvalidPrice },
        { "negative price", FinancialErrorCode.InvalidPrice },
        { "zero quantity", FinancialErrorCode.InvalidQuantity },
        { "negative quantity", FinancialErrorCode.InvalidQuantity },
        { "quantity above bound", FinancialErrorCode.InvalidQuantity },
        { "unsupported step", FinancialErrorCode.InvalidUnitStep },
        { "misaligned quantity", FinancialErrorCode.QuantityNotAlignedToStep },
        { "rate above 100%", FinancialErrorCode.InvalidRate },
        { "negative rate", FinancialErrorCode.InvalidRate },
        { "currency mismatch", FinancialErrorCode.MixedCurrency },
        { "compound tax", FinancialErrorCode.UnsupportedPolicy },
        { "taxable without component", FinancialErrorCode.InvalidTaxPolicy },
        { "exempt with component", FinancialErrorCode.InvalidTaxPolicy },
        { "unknown treatment", FinancialErrorCode.UnknownTaxTreatment },
        { "duplicate line number", FinancialErrorCode.DuplicateLineNumber },
        { "line number zero", FinancialErrorCode.InvalidLineNumber },
    };

    [Theory]
    [MemberData(nameof(InvalidCarts))]
    public void F08_invalid_cart_is_rejected(string scenario, FinancialErrorCode expected)
    {
        CartLine[] lines = scenario switch
        {
            "zero price" => [Line(1, 0)],
            "negative price" => [Line(1, -1)],
            "zero quantity" => [Line(1, 1_00, quantityQ: 0)],
            "negative quantity" => [Line(1, 1_00, quantityQ: -1000)],
            "quantity above bound" => [Line(1, 1, quantityQ: FinancialLimits.MaxQuantityQ + 1000)],
            "unsupported step" => [Line(1, 1_00, quantityQ: 1000, stepQ: 500)],
            "misaligned quantity" => [Line(1, 1_00, quantityQ: 1500)],
            "rate above 100%" => [Line(1, 1_00, tax: LineTaxRule.Taxable(PriceMode.Exclusive, "SYN", 1_000_001))],
            "negative rate" => [Line(1, 1_00, tax: LineTaxRule.Taxable(PriceMode.Exclusive, "SYN", -1))],
            "currency mismatch" => [Line(1, 1_00), Line(2, 1_00, currency: new Currency("USD", 2))],
            "compound tax" => [Line(1, 1_00, tax: new LineTaxRule(PriceMode.Exclusive, TreatmentCode.Taxable, [new("A", 1), new("B", 1)]))],
            "taxable without component" => [Line(1, 1_00, tax: new LineTaxRule(PriceMode.Exclusive, TreatmentCode.Taxable, []))],
            "exempt with component" => [Line(1, 1_00, tax: new LineTaxRule(PriceMode.Exclusive, TreatmentCode.Exempt, [new("A", 0)]))],
            "unknown treatment" => [Line(1, 1_00, tax: new LineTaxRule(PriceMode.Exclusive, TreatmentCode.Unknown, []))],
            "duplicate line number" => [Line(1, 1_00), Line(1, 2_00)],
            "line number zero" => [Line(0, 1_00)],
            _ => throw new InvalidOperationException(scenario),
        };

        Assert.Equal(expected, Rejects(() => Calc(lines)));
    }

    [Fact]
    public void F08_empty_and_oversized_carts_are_rejected()
    {
        Assert.Equal(FinancialErrorCode.EmptyCart, Rejects(() => Calc()));
        var tooMany = Enumerable.Range(1, FinancialLimits.MaxLinesPerCart + 1).Select(i => Line(i, 1_00)).ToArray();
        Assert.Equal(FinancialErrorCode.TooManyLines, Rejects(() => Calc(tooMany)));
    }

    [Fact]
    public void F08_discount_rules()
    {
        Assert.Equal(FinancialErrorCode.DiscountExceedsEligibleAmount, Rejects(() => Calc(OrderDiscount.Fixed(1_01), Line(1, 1_00))));
        Assert.Equal(FinancialErrorCode.DiscountExceedsEligibleAmount, Rejects(() => Calc(OrderDiscount.Fixed(1), Line(1, 1_00, eligible: false))));
        Assert.Equal(FinancialErrorCode.InvalidDiscount, Rejects(() => Calc(OrderDiscount.Fixed(-1), Line(1, 1_00))));
        Assert.Equal(FinancialErrorCode.InvalidDiscount, Rejects(() => Calc(OrderDiscount.Percent(1_000_001), Line(1, 1_00))));
        Assert.Equal(
            FinancialErrorCode.UnsupportedPolicy,
            Rejects(() => Calc(OrderDiscount.Fixed(1_00), Line(1, 10_00, tax: Inclusive12), Line(2, 10_00, tax: Exclusive12))));
    }

    [Fact]
    public void Mixed_price_modes_without_discount_are_calculated_line_by_line()
    {
        var order = Calc(Line(1, 112_00, tax: Inclusive12), Line(2, 100_00, tax: Exclusive12));
        Assert.Equal(224_00, order.TotalMinor);
        Assert.Equal(24_00, order.TaxMinor);
    }

    [Fact]
    public void F08_profile_rules()
    {
        var draft = TestProfile with { ApprovalState = ProfileApprovalState.Draft };
        Assert.Equal(FinancialErrorCode.ProfileNotUsable, Rejects(() => OrderCalculator.Calculate(draft, [Line(1, 1_00)], OrderDiscount.None)));

        var otherStrategy = TestProfile with { StrategyKey = "statutory-v9" };
        Assert.Equal(FinancialErrorCode.UnsupportedPolicy, Rejects(() => OrderCalculator.Calculate(otherStrategy, [Line(1, 1_00)], OrderDiscount.None)));

        // Synthetic no-tax treatment can never leak into an Approved profile.
        Assert.Equal(FinancialErrorCode.ProfileNotUsable, Rejects(() => OrderCalculator.Calculate(ApprovedProfile, [Line(1, 1_00)], OrderDiscount.None)));
    }

    [Fact]
    public void F08_invalid_currency_definition()
    {
        Assert.Equal(FinancialErrorCode.InvalidCurrency, Rejects(() => _ = new Currency("PHP", 5)));
        Assert.Equal(FinancialErrorCode.InvalidCurrency, Rejects(() => _ = new Currency("php", 2)));
        Assert.Equal(FinancialErrorCode.InvalidCurrency, Rejects(() => _ = new Currency("PH", 2)));
    }

    [Fact]
    public void Cash_tender_rules()
    {
        Assert.Equal(FinancialErrorCode.InsufficientTender, Rejects(() => CashTender.Settle(250_00, 249_99)));
        Assert.Equal(FinancialErrorCode.NonPositiveTotal, Rejects(() => CashTender.Settle(0, 0)));
        Assert.Equal(new CashSettlement(250_00, 250_00, 0), CashTender.Settle(250_00, 250_00));
    }

    [Fact]
    public void Correction_quantity_rules()
    {
        var original = new OriginalLineComponents(3000, 1000, 89_29, 4_46, 5_00, [new OriginalTaxComponent("SYN", 10_71)]);

        Assert.Equal(FinancialErrorCode.InvalidCorrectionQuantity, Rejects(() => CorrectionAllocator.Allocate(original, 0, 0)));
        Assert.Equal(FinancialErrorCode.InvalidCorrectionQuantity, Rejects(() => CorrectionAllocator.Allocate(original, 0, 500)));
        Assert.Equal(FinancialErrorCode.InvalidCorrectionQuantity, Rejects(() => CorrectionAllocator.Allocate(original, -1000, 1000)));
        Assert.Equal(FinancialErrorCode.OverRefund, Rejects(() => CorrectionAllocator.Allocate(original, 3000, 1000)));
    }

    [Fact]
    public void F12_line_basis_above_document_bound_is_rejected()
    {
        // 100,000,000,000 minor × 1,000,000 units: computed exactly in 128 bits, then rejected.
        var ex = Rejects(() => Calc(Line(1, FinancialLimits.MaxDocumentMinor, quantityQ: FinancialLimits.MaxQuantityQ)));
        Assert.Equal(FinancialErrorCode.AmountOutOfRange, ex);
    }

    [Fact]
    public void F12_order_total_above_document_bound_is_rejected()
    {
        // Each line is exactly at the bound; their sum is not.
        var atBound = Calc(Line(1, FinancialLimits.MaxDocumentMinor));
        Assert.Equal(FinancialLimits.MaxDocumentMinor, atBound.TotalMinor);

        var ex = Rejects(() => Calc(Line(1, FinancialLimits.MaxDocumentMinor), Line(2, 1)));
        Assert.Equal(FinancialErrorCode.AmountOutOfRange, ex);
    }

    [Fact]
    public void F12_integer_sum_overflow_is_detected_not_wrapped()
    {
        Assert.Equal(FinancialErrorCode.AmountOutOfRange, Rejects(() => ExactMath.CheckedSum([long.MaxValue, 1])));
        Assert.Equal(FinancialErrorCode.AmountOutOfRange, Rejects(() => ExactMath.CheckedSum([long.MinValue, -1])));
    }

    [Fact]
    public void F12_maximum_rate_and_amount_compute_exactly()
    {
        var full = LineTaxRule.Taxable(PriceMode.Inclusive, "SYN", FinancialLimits.RateScalePpm);
        var line = Assert.Single(Calc(Line(1, FinancialLimits.MaxDocumentMinor, tax: full)).Lines);

        Assert.Equal(FinancialLimits.MaxDocumentMinor / 2, line.NetMinor);
        Assert.Equal(FinancialLimits.MaxDocumentMinor / 2, line.TaxMinor);
        Assert.Equal(FinancialLimits.MaxDocumentMinor, line.TotalMinor);
    }
}
