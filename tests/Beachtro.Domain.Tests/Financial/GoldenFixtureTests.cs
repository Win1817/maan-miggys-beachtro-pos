using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Financial.Corrections;
using Beachtro.Domain.Financial.Payments;
using Beachtro.Domain.Financial.Shifts;
using static Beachtro.Domain.Tests.Financial.Fixtures;

namespace Beachtro.Domain.Tests.Financial;

/// <summary>Golden cases F-01–F-07 and F-10 from the financial processing design, exact integer amounts.</summary>
public sealed class GoldenFixtureTests
{
    [Fact]
    public void F01_no_tax_cash_sale()
    {
        var order = Calc(Line(1, 100_00, units: 2), Line(2, 50_00));

        Assert.Equal(250_00, order.NetMinor);
        Assert.Equal(250_00, order.TotalMinor);
        Assert.Equal(0, order.TaxMinor);
        Assert.Equal([200_00L, 50_00L], order.Lines.Select(l => l.TotalMinor));

        var cash = CashTender.Settle(order.TotalMinor, 300_00);
        Assert.Equal(new CashSettlement(250_00, 300_00, 50_00), cash);
    }

    [Fact]
    public void F02_half_minor_unit_rounds_away_from_zero()
    {
        var order = Calc(Line(1, unitPriceMinor: 1, quantityQ: 500, stepQ: FinancialLimits.FractionalStepQ));

        var line = Assert.Single(order.Lines);
        Assert.Equal(500, line.QuantityQ);
        Assert.Equal(1, line.PriceBasisMinor);
        Assert.Equal(1, line.NetMinor);
        Assert.Equal(1, order.TotalMinor);
    }

    [Fact]
    public void F02_whole_step_product_rejects_half_unit()
    {
        var ex = Assert.Throws<FinancialRuleException>(() => Calc(Line(1, unitPriceMinor: 1, quantityQ: 500)));
        Assert.Equal(FinancialErrorCode.QuantityNotAlignedToStep, ex.Code);
    }

    [Fact]
    public void F03_inclusive_tax_is_extracted_exactly()
    {
        var line = Assert.Single(Calc(Line(1, 112_00, tax: Inclusive12)).Lines);

        Assert.Equal(100_00, line.NetMinor);
        Assert.Equal(12_00, line.TaxMinor);
        Assert.Equal(112_00, line.TotalMinor);
        var component = Assert.Single(line.TaxComponents);
        Assert.Equal(new TaxComponentAmount("SYN", SyntheticRatePpm, 100_00, 12_00), component);
    }

    [Fact]
    public void F04_inclusive_with_ten_percent_discount()
    {
        var line = Assert.Single(Calc(OrderDiscount.Percent(100_000), Line(1, 112_00, tax: Inclusive12)).Lines);

        Assert.Equal(11_20, line.DiscountBasisMinor);
        Assert.Equal(100_00, line.NetBeforeDiscountMinor);
        Assert.Equal(90_00, line.NetMinor);
        Assert.Equal(10_00, line.DiscountExTaxMinor);
        Assert.Equal(10_80, line.TaxMinor);
        Assert.Equal(100_80, line.TotalMinor);
    }

    [Fact]
    public void F05_exclusive_with_ten_percent_discount()
    {
        var line = Assert.Single(Calc(OrderDiscount.Percent(100_000), Line(1, 100_00, tax: Exclusive12)).Lines);

        Assert.Equal(10_00, line.DiscountBasisMinor);
        Assert.Equal(10_00, line.DiscountExTaxMinor);
        Assert.Equal(90_00, line.NetMinor);
        Assert.Equal(10_80, line.TaxMinor);
        Assert.Equal(100_80, line.TotalMinor);
    }

    [Fact]
    public void F06_allocation_tie_goes_to_lowest_line_number()
    {
        var order = Calc(OrderDiscount.Fixed(1), Line(1, 1_00), Line(2, 1_00), Line(3, 1_00));

        Assert.Equal([1L, 0L, 0L], order.Lines.Select(l => l.DiscountBasisMinor));
        Assert.Equal(1, order.DiscountBasisMinor);
        Assert.Equal(2_99, order.TotalMinor);
    }

    [Fact]
    public void F06_allocation_does_not_depend_on_input_order()
    {
        var forward = Calc(OrderDiscount.Fixed(1), Line(1, 1_00), Line(2, 1_00), Line(3, 1_00));
        var reversed = Calc(OrderDiscount.Fixed(1), Line(3, 1_00), Line(2, 1_00), Line(1, 1_00));

        Assert.Equal(forward.Lines.Select(ValueOf), reversed.Lines.Select(ValueOf));
        Assert.Equal(forward.TotalMinor, reversed.TotalMinor);
    }

    [Fact]
    public void F07_original_inclusive_residual()
    {
        var line = Assert.Single(Calc(OrderDiscount.Fixed(5_00), Line(1, 35_00, units: 3, tax: Inclusive12)).Lines);

        Assert.Equal(3000, line.QuantityQ);
        Assert.Equal(105_00, line.PriceBasisMinor);
        Assert.Equal(5_00, line.DiscountBasisMinor);
        Assert.Equal(93_75, line.NetBeforeDiscountMinor);
        Assert.Equal(4_46, line.DiscountExTaxMinor);
        Assert.Equal(89_29, line.NetMinor);
        Assert.Equal(10_71, line.TaxMinor);
        Assert.Equal(100_00, line.TotalMinor);
    }

    [Fact]
    public void F07_three_single_unit_refunds_return_every_minor_unit()
    {
        var line = Assert.Single(Calc(OrderDiscount.Fixed(5_00), Line(1, 35_00, units: 3, tax: Inclusive12)).Lines);
        var original = new OriginalLineComponents(
            line.QuantityQ,
            line.UnitStepQ,
            line.NetMinor,
            line.DiscountExTaxMinor,
            line.DiscountBasisMinor,
            line.TaxComponents.Select(t => new OriginalTaxComponent(t.Key, t.TaxMinor)).ToArray());

        var r1 = CorrectionAllocator.Allocate(original, 0, 1000);
        var r2 = CorrectionAllocator.Allocate(original, 1000, 1000);
        var r3 = CorrectionAllocator.Allocate(original, 2000, 1000);

        Assert.Equal([29_76L, 29_76L, 29_77L], new[] { r1, r2, r3 }.Select(r => r.NetMinor));
        Assert.Equal([3_57L, 3_57L, 3_57L], new[] { r1, r2, r3 }.Select(r => r.TaxMinor));
        Assert.Equal([33_33L, 33_33L, 33_34L], new[] { r1, r2, r3 }.Select(r => r.TotalMinor));
        Assert.Equal([1_48L, 1_49L, 1_49L], new[] { r1, r2, r3 }.Select(r => r.DiscountExTaxMinor));
        Assert.Equal([1000L, 2000L, 3000L], new[] { r1, r2, r3 }.Select(r => r.CumulativeReturnedQ));

        Assert.Equal(line.NetMinor, r1.NetMinor + r2.NetMinor + r3.NetMinor);
        Assert.Equal(line.TaxMinor, r1.TaxMinor + r2.TaxMinor + r3.TaxMinor);
        Assert.Equal(line.TotalMinor, r1.TotalMinor + r2.TotalMinor + r3.TotalMinor);
        Assert.Equal(line.DiscountBasisMinor, r1.DiscountBasisMinor + r2.DiscountBasisMinor + r3.DiscountBasisMinor);
    }

    [Fact]
    public void F07_refund_beyond_original_quantity_is_rejected()
    {
        var original = new OriginalLineComponents(3000, 1000, 89_29, 4_46, 5_00, [new OriginalTaxComponent("SYN", 10_71)]);

        var ex = Assert.Throws<FinancialRuleException>(() => CorrectionAllocator.Allocate(original, 2000, 2000));
        Assert.Equal(FinancialErrorCode.OverRefund, ex.Code);
    }

    [Fact]
    public void F10_expected_shift_cash_excludes_gcash_and_tendered_change()
    {
        // Opening 100, cash sale 250 (tendered 300, change 50), cash-in 20, cash-out 10,
        // cash refund 40; the 112 GCash sale is not drawer cash.
        var expected = ShiftCashCalculator.Expected(
            openingMinor: 100_00, cashSalesAppliedMinor: 250_00, cashInMinor: 20_00, cashOutMinor: 10_00, cashRefundsMinor: 40_00);

        Assert.Equal(320_00, expected);
        Assert.Equal(-5_00, ShiftCashCalculator.Variance(315_00, expected));
    }
}
