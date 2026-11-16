namespace Beachtro.Domain.Financial.Calculation;

/// <summary>
/// Generic line-rounding strategy v1 (finance design §Deterministic generic algorithms A–C).
/// Supports no-tax and one ad-valorem component per line, inclusive or exclusive pricing, and one
/// non-stacking order discount allocated by largest remainder. Anything else is rejected rather
/// than silently approximated.
/// </summary>
public static class OrderCalculator
{
    public const string StrategyKey = "generic-line-v1";

    private const long RateScale = FinancialLimits.RateScalePpm;

    public static OrderCalculation Calculate(CalculationProfile profile, IReadOnlyList<CartLine> lines, OrderDiscount discount)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(discount);

        ValidateProfile(profile);
        var ordered = ValidateLines(profile, lines);

        // A. Extended price basis per line: B = RoundM(U × Q / 1000); no early unit rounding.
        var basis = new long[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var line = ordered[i];
            basis[i] = ExactMath.RequireDocumentAmount(
                ExactMath.RoundHalfAwayFromZero((Int128)line.UnitPriceMinor * line.QuantityQ, FinancialLimits.QuantityScale),
                $"Line {line.LineNo} price basis");
        }

        // B. One generic order discount, allocated by largest remainder.
        var allocation = AllocateDiscount(ordered, basis, discount);

        // C. Tax and financial components per line.
        var results = new LineCalculation[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            results[i] = CalculateLine(ordered[i], basis[i], allocation[i]);
        }

        var total = ExactMath.RequireDocumentAmount(ExactMath.CheckedSum(results.Select(r => r.TotalMinor)), "Order total");

        return new OrderCalculation(
            profile.Currency,
            profile.Id,
            profile.Revision,
            StrategyKey,
            results,
            ExactMath.RequireDocumentAmount(ExactMath.CheckedSum(results.Select(r => r.PriceBasisMinor)), "Price basis subtotal"),
            ExactMath.CheckedSum(results.Select(r => r.DiscountBasisMinor)),
            ExactMath.CheckedSum(results.Select(r => r.NetBeforeDiscountMinor)),
            ExactMath.CheckedSum(results.Select(r => r.DiscountExTaxMinor)),
            ExactMath.CheckedSum(results.Select(r => r.NetMinor)),
            ExactMath.CheckedSum(results.Select(r => r.TaxMinor)),
            total);
    }

    private static void ValidateProfile(CalculationProfile profile)
    {
        if (profile.ApprovalState == ProfileApprovalState.Draft)
        {
            throw new FinancialRuleException(FinancialErrorCode.ProfileNotUsable, "A Draft calculation profile cannot be used for quotes.");
        }

        if (!string.Equals(profile.StrategyKey, StrategyKey, StringComparison.Ordinal))
        {
            throw new FinancialRuleException(FinancialErrorCode.UnsupportedPolicy, $"Calculation strategy '{profile.StrategyKey}' is not supported by {StrategyKey}.");
        }
    }

    private static CartLine[] ValidateLines(CalculationProfile profile, IReadOnlyList<CartLine> lines)
    {
        if (lines.Count == 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.EmptyCart, "The cart has no lines.");
        }

        if (lines.Count > FinancialLimits.MaxLinesPerCart)
        {
            throw new FinancialRuleException(FinancialErrorCode.TooManyLines, $"A cart may have at most {FinancialLimits.MaxLinesPerCart} lines.");
        }

        var seen = new HashSet<int>();
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (line.LineNo is < 1 or > FinancialLimits.MaxLinesPerCart)
            {
                throw new FinancialRuleException(FinancialErrorCode.InvalidLineNumber, $"Line number {line.LineNo} is outside 1..{FinancialLimits.MaxLinesPerCart}.");
            }

            if (!seen.Add(line.LineNo))
            {
                throw new FinancialRuleException(FinancialErrorCode.DuplicateLineNumber, $"Line number {line.LineNo} appears twice.");
            }

            if (line.Currency != profile.Currency)
            {
                throw new FinancialRuleException(FinancialErrorCode.MixedCurrency, $"Line {line.LineNo} currency {line.Currency} differs from profile currency {profile.Currency}.");
            }

            if (line.UnitPriceMinor <= 0 || line.UnitPriceMinor > FinancialLimits.MaxDocumentMinor)
            {
                throw new FinancialRuleException(FinancialErrorCode.InvalidPrice, $"Line {line.LineNo} unit price must be positive and at most {FinancialLimits.MaxDocumentMinor}.");
            }

            if (line.UnitStepQ is not (FinancialLimits.WholeUnitStepQ or FinancialLimits.FractionalStepQ))
            {
                throw new FinancialRuleException(FinancialErrorCode.InvalidUnitStep, $"Line {line.LineNo} unit step {line.UnitStepQ} is not supported.");
            }

            if (line.QuantityQ <= 0 || line.QuantityQ > FinancialLimits.MaxQuantityQ)
            {
                throw new FinancialRuleException(FinancialErrorCode.InvalidQuantity, $"Line {line.LineNo} quantity must be positive and at most {FinancialLimits.MaxQuantityQ}Q.");
            }

            if (line.QuantityQ % line.UnitStepQ != 0)
            {
                throw new FinancialRuleException(FinancialErrorCode.QuantityNotAlignedToStep, $"Line {line.LineNo} quantity {line.QuantityQ}Q is not a multiple of step {line.UnitStepQ}Q.");
            }

            ValidateTax(profile, line);
        }

        return lines.OrderBy(l => l.LineNo).ToArray();
    }

    private static void ValidateTax(CalculationProfile profile, CartLine line)
    {
        var tax = line.Tax ?? throw new FinancialRuleException(FinancialErrorCode.InvalidTaxPolicy, $"Line {line.LineNo} has no tax rule.");
        var components = tax.Components ?? [];

        if (!Enum.IsDefined(tax.PriceMode))
        {
            throw new FinancialRuleException(FinancialErrorCode.InvalidTaxPolicy, $"Line {line.LineNo} price mode is invalid.");
        }

        switch (tax.Treatment)
        {
            case TreatmentCode.Unknown:
                throw new FinancialRuleException(FinancialErrorCode.UnknownTaxTreatment, $"Line {line.LineNo} has an Unknown tax treatment; it cannot be calculated.");
            case TreatmentCode.Taxable:
                if (components.Count == 0)
                {
                    throw new FinancialRuleException(FinancialErrorCode.InvalidTaxPolicy, $"Line {line.LineNo} is Taxable but has no tax component.");
                }

                if (components.Count > 1)
                {
                    throw new FinancialRuleException(FinancialErrorCode.UnsupportedPolicy, $"Line {line.LineNo} has compound tax components; {StrategyKey} supports one.");
                }

                var c = components[0];
                if (string.IsNullOrWhiteSpace(c.Key))
                {
                    throw new FinancialRuleException(FinancialErrorCode.InvalidTaxPolicy, $"Line {line.LineNo} tax component has no key.");
                }

                if (c.RatePpm is < 0 or > FinancialLimits.RateScalePpm)
                {
                    throw new FinancialRuleException(FinancialErrorCode.InvalidRate, $"Line {line.LineNo} rate {c.RatePpm}ppm is outside 0..{FinancialLimits.RateScalePpm}.");
                }

                break;
            case TreatmentCode.Exempt:
            case TreatmentCode.ZeroRated:
            case TreatmentCode.TestNoTax:
                if (components.Count != 0)
                {
                    throw new FinancialRuleException(FinancialErrorCode.InvalidTaxPolicy, $"Line {line.LineNo} treatment {tax.Treatment} must not carry tax components.");
                }

                if (tax.Treatment == TreatmentCode.TestNoTax && profile.ApprovalState != ProfileApprovalState.Test)
                {
                    throw new FinancialRuleException(FinancialErrorCode.ProfileNotUsable, "TestNoTax treatment is allowed only under a Test calculation profile.");
                }

                break;
            default:
                throw new FinancialRuleException(FinancialErrorCode.InvalidTaxPolicy, $"Line {line.LineNo} treatment is invalid.");
        }
    }

    private static long[] AllocateDiscount(CartLine[] lines, long[] basis, OrderDiscount discount)
    {
        var allocation = new long[lines.Length];
        if (discount.Kind == OrderDiscountKind.None)
        {
            if (discount.Value != 0)
            {
                throw new FinancialRuleException(FinancialErrorCode.InvalidDiscount, "A None discount must have value 0.");
            }

            return allocation;
        }

        var eligible = Enumerable.Range(0, lines.Length).Where(i => lines[i].DiscountEligible).ToArray();
        var eligibleTotal = ExactMath.CheckedSum(eligible.Select(i => basis[i]));

        long target;
        switch (discount.Kind)
        {
            case OrderDiscountKind.FixedMinor:
                if (discount.Value < 0)
                {
                    throw new FinancialRuleException(FinancialErrorCode.InvalidDiscount, "A fixed discount cannot be negative.");
                }

                if (discount.Value > eligibleTotal)
                {
                    throw new FinancialRuleException(FinancialErrorCode.DiscountExceedsEligibleAmount, $"Discount {discount.Value} exceeds the eligible amount {eligibleTotal}.");
                }

                target = discount.Value;
                break;
            case OrderDiscountKind.PercentPpm:
                if (discount.Value is < 0 or > FinancialLimits.RateScalePpm)
                {
                    throw new FinancialRuleException(FinancialErrorCode.InvalidDiscount, $"Discount rate {discount.Value}ppm is outside 0..{FinancialLimits.RateScalePpm}.");
                }

                target = Math.Min(ExactMath.RoundHalfAwayFromZero((Int128)eligibleTotal * discount.Value, RateScale), eligibleTotal);
                break;
            default:
                throw new FinancialRuleException(FinancialErrorCode.InvalidDiscount, "Unknown discount kind.");
        }

        if (target == 0)
        {
            return allocation;
        }

        // A single fixed discount is never spread over unlike price bases.
        if (eligible.Select(i => lines[i].Tax.PriceMode).Distinct().Count() > 1)
        {
            throw new FinancialRuleException(FinancialErrorCode.UnsupportedPolicy, "Discount-eligible lines mix inclusive and exclusive pricing.");
        }

        // Largest remainder: a_i = floor(D·B_i/E); leftover units go to the largest remainders,
        // ties broken by ascending LineNo (lines are already in LineNo order).
        var remainders = new Int128[lines.Length];
        long allocated = 0;
        foreach (var i in eligible)
        {
            var numerator = (Int128)target * basis[i];
            allocation[i] = ExactMath.FloorNonNegative(numerator, eligibleTotal);
            remainders[i] = numerator % eligibleTotal;
            allocated += allocation[i];
        }

        var leftover = target - allocated;
        foreach (var i in eligible.OrderByDescending(i => remainders[i]).ThenBy(i => lines[i].LineNo).Take((int)leftover))
        {
            allocation[i] += 1;
        }

        return allocation;
    }

    private static LineCalculation CalculateLine(CartLine line, long priceBasis, long discountBasis)
    {
        var discounted = priceBasis - discountBasis; // P
        long netBefore, net, tax;
        TaxComponentAmount[] components;

        if (line.Tax.Treatment == TreatmentCode.Taxable)
        {
            var component = line.Tax.Components[0];
            Int128 rate = component.RatePpm;
            if (line.Tax.PriceMode == PriceMode.Exclusive)
            {
                netBefore = priceBasis;
                net = discounted;
                tax = ExactMath.RoundHalfAwayFromZero(net * rate, RateScale);
            }
            else
            {
                // Inclusive: net is rounded, tax is the residual so payable stays exactly P.
                netBefore = ExactMath.RoundHalfAwayFromZero((Int128)priceBasis * RateScale, RateScale + rate);
                net = ExactMath.RoundHalfAwayFromZero((Int128)discounted * RateScale, RateScale + rate);
                tax = discounted - net;
            }

            components = [new TaxComponentAmount(component.Key, component.RatePpm, net, tax)];
        }
        else
        {
            netBefore = priceBasis;
            net = discounted;
            tax = 0;
            components = [];
        }

        var discountExTax = netBefore - net;
        var total = net + tax;

        if (net < 0 || tax < 0 || discountExTax < 0 || netBefore < 0)
        {
            throw new FinancialRuleException(FinancialErrorCode.AmountOutOfRange, $"Line {line.LineNo} produced a negative component.");
        }

        ExactMath.RequireDocumentAmount(total, $"Line {line.LineNo} total");

        return new LineCalculation(
            line.LineNo,
            line.ProductId,
            line.UnitPriceMinor,
            line.QuantityQ,
            line.UnitStepQ,
            line.Tax.PriceMode,
            line.Tax.Treatment,
            priceBasis,
            discountBasis,
            netBefore,
            discountExTax,
            net,
            tax,
            total,
            components);
    }
}
