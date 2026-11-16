using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Financial.Corrections;
using static Beachtro.Domain.Tests.Financial.Fixtures;

namespace Beachtro.Domain.Tests.Financial;

/// <summary>
/// Generated cases (T-04) with fixed seeds so any failure reproduces exactly. They check the
/// identities every persisted calculation must satisfy, independent of the golden fixtures.
/// </summary>
public sealed class InvariantTests
{
    [Fact]
    public void Rounding_is_half_away_from_zero_in_both_directions()
    {
        Assert.Equal(1, ExactMath.RoundHalfAwayFromZero(5, 10));
        Assert.Equal(-1, ExactMath.RoundHalfAwayFromZero(-5, 10));
        Assert.Equal(0, ExactMath.RoundHalfAwayFromZero(4, 10));
        Assert.Equal(0, ExactMath.RoundHalfAwayFromZero(-4, 10));
        Assert.Equal(2, ExactMath.RoundHalfAwayFromZero(15, 10));
        Assert.Equal(-2, ExactMath.RoundHalfAwayFromZero(-15, 10));
        Assert.Equal(2, ExactMath.RoundHalfAwayFromZero(7, 3)); // 2.33 rounds down
        Assert.Equal(3, ExactMath.RoundHalfAwayFromZero(8, 3)); // 2.67 rounds up
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Generated_orders_satisfy_component_identities(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var mode = random.Next(2) == 0 ? PriceMode.Inclusive : PriceMode.Exclusive;
            var lineCount = random.Next(1, 12);
            var lines = Enumerable.Range(1, lineCount).Select(n => RandomLine(random, n, mode)).ToArray();
            var discount = RandomDiscount(random, lines);

            OrderCalculation order;
            try
            {
                order = Calc(discount, lines);
            }
            catch (FinancialRuleException ex) when (ex.Code == FinancialErrorCode.DiscountExceedsEligibleAmount)
            {
                continue;
            }

            foreach (var l in order.Lines)
            {
                Assert.Equal(l.TotalMinor, l.NetMinor + l.TaxMinor);
                Assert.Equal(l.NetBeforeDiscountMinor, l.NetMinor + l.DiscountExTaxMinor);
                Assert.True(l.DiscountBasisMinor >= 0 && l.DiscountBasisMinor <= l.PriceBasisMinor);
                Assert.True(l.NetMinor >= 0 && l.TaxMinor >= 0 && l.DiscountExTaxMinor >= 0);
                Assert.Equal(l.TaxMinor, l.TaxComponents.Sum(c => c.TaxMinor));

                var discounted = l.PriceBasisMinor - l.DiscountBasisMinor;
                if (l.PriceMode == PriceMode.Inclusive || l.Treatment != TreatmentCode.Taxable)
                {
                    Assert.Equal(discounted, l.TotalMinor); // inclusive payable is exactly P
                }
                else
                {
                    Assert.Equal(discounted, l.NetMinor);
                }
            }

            Assert.Equal(order.TotalMinor, order.Lines.Sum(l => l.TotalMinor));
            Assert.Equal(order.NetMinor + order.TaxMinor, order.TotalMinor);
            Assert.Equal(order.DiscountBasisMinor, order.Lines.Sum(l => l.DiscountBasisMinor));

            // lines are generated in LineNo order, so index i matches order.Lines[i].
            var eligibleTotal = order.Lines.Where((_, i) => lines[i].DiscountEligible).Sum(l => l.PriceBasisMinor);
            var expectedDiscount = discount.Kind switch
            {
                OrderDiscountKind.FixedMinor => discount.Value,
                OrderDiscountKind.PercentPpm => ExactMath.RoundHalfAwayFromZero((Int128)eligibleTotal * discount.Value, 1_000_000),
                _ => 0,
            };
            Assert.Equal(expectedDiscount, order.DiscountBasisMinor);

            // Deterministic: the same inputs in another order produce identical results.
            var again = Calc(discount, lines.Reverse().ToArray());
            Assert.Equal(order.Lines.Select(Fixtures.ValueOf), again.Lines.Select(Fixtures.ValueOf));
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Any_sequence_of_partial_refunds_returns_exactly_the_original(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var units = random.Next(1, 20);
            var line = Assert.Single(
                Calc(OrderDiscount.Percent(random.Next(0, 300_000)), RandomLine(random, 1, PriceMode.Inclusive, units)).Lines);
            var original = new OriginalLineComponents(
                line.QuantityQ,
                line.UnitStepQ,
                line.NetMinor,
                line.DiscountExTaxMinor,
                line.DiscountBasisMinor,
                line.TaxComponents.Select(t => new OriginalTaxComponent(t.Key, t.TaxMinor)).ToArray());

            long returned = 0, net = 0, tax = 0, total = 0, discountExTax = 0;
            while (returned < line.QuantityQ)
            {
                var remainingUnits = (line.QuantityQ - returned) / 1000;
                var step = random.Next(1, (int)remainingUnits + 1) * 1000L;
                var r = CorrectionAllocator.Allocate(original, returned, step);

                Assert.True(r.NetMinor >= 0 && r.TaxMinor >= 0 && r.DiscountExTaxMinor >= 0);
                Assert.Equal(r.TotalMinor, r.NetMinor + r.TaxMinor);

                returned = r.CumulativeReturnedQ;
                net += r.NetMinor;
                tax += r.TaxMinor;
                total += r.TotalMinor;
                discountExTax += r.DiscountExTaxMinor;

                Assert.True(net <= line.NetMinor && tax <= line.TaxMinor, "cumulative reversal never exceeds the original");
            }

            Assert.Equal(line.NetMinor, net);
            Assert.Equal(line.TaxMinor, tax);
            Assert.Equal(line.TotalMinor, total);
            Assert.Equal(line.DiscountExTaxMinor, discountExTax);
        }
    }

    private static CartLine RandomLine(Random random, int lineNo, PriceMode mode, int? units = null)
    {
        var tax = random.Next(4) switch
        {
            0 => new LineTaxRule(mode, TreatmentCode.TestNoTax, []),
            1 => LineTaxRule.Taxable(mode, "SYN", 0),
            _ => LineTaxRule.Taxable(mode, "SYN", random.Next(1, 300_000)),
        };
        return Line(lineNo, random.Next(1, 500_000), units: units ?? random.Next(1, 25), tax: tax, eligible: random.Next(5) != 0);
    }

    private static OrderDiscount RandomDiscount(Random random, CartLine[] lines) =>
        random.Next(3) switch
        {
            0 => OrderDiscount.None,
            1 => OrderDiscount.Percent(random.Next(0, 1_000_001)),
            _ => OrderDiscount.Fixed(random.Next(0, 100_000)),
        };
}
