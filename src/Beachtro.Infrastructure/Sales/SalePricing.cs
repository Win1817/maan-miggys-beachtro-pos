using System.Text.Json;
using Beachtro.Application.Common;
using Beachtro.Application.Configuration;
using Beachtro.Application.Sales;
using Beachtro.Domain.Financial;
using Beachtro.Domain.Financial.Calculation;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Persistence;
using Beachtro.Infrastructure.Persistence.Entities;
using Beachtro.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Beachtro.Infrastructure.Sales;

/// <summary>A quote plus the facts it was priced from, for the completion transaction.</summary>
internal sealed record PricedCart(CheckoutQuote Quote, EffectiveCalculationProfile Profile, IReadOnlyDictionary<Guid, Product> Products);

/// <summary>
/// Prices a cart server-side under the effective profile (finance design §Checkout protocol step 1)
/// and produces two hashes: the cart fingerprint (what the operator asked for) and the quote hash
/// (every priced fact: product versions, prices, policy revision and all computed amounts).
/// </summary>
internal static class SalePricing
{
    public static async Task<Result<PricedCart>> PriceAsync(PosDbContext db, IProfileService profiles, CartInput cart, long nowUtc, CancellationToken cancellationToken)
    {
        if (cart.Lines is null || cart.Lines.Count == 0)
        {
            return Result<PricedCart>.Fail(ResultStatus.Validation, "The cart is empty.");
        }

        if (!Enum.IsDefined(cart.OrderType))
        {
            return Result<PricedCart>.Fail(ResultStatus.Validation, "Choose Dine-in or Takeout.");
        }

        var effective = await profiles.GetEffectiveCalculationProfileAsync(nowUtc, cancellationToken).ConfigureAwait(false);
        if (!effective.IsSuccess)
        {
            return Result<PricedCart>.Fail(ResultStatus.Validation, "No calculation profile is in effect; sales cannot be priced.");
        }

        var ids = cart.Lines.Select(l => l.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        var profile = effective.Value!;
        var lines = new List<CartLine>(cart.Lines.Count);
        for (var i = 0; i < cart.Lines.Count; i++)
        {
            var input = cart.Lines[i];
            if (!products.TryGetValue(input.ProductId, out var product) || !product.IsActive)
            {
                return Result<PricedCart>.Fail(ResultStatus.Validation, $"Line {i + 1}: the product is not available for sale.");
            }

            if (!profile.TaxRules.TryGetValue(product.TaxKey, out var taxRule))
            {
                return Result<PricedCart>.Fail(ResultStatus.Validation, $"Line {i + 1}: {product.Name} has no tax entry in the current profile.");
            }

            lines.Add(new CartLine(i + 1, product.Id, product.UnitPriceM, input.QuantityQ, product.UnitStepQ,
                new Currency(product.CurrencyCode, product.CurrencyExponent), DiscountEligible: true, taxRule));
        }

        var discount = cart.Discount ?? OrderDiscount.None;
        OrderCalculation calculation;
        try
        {
            calculation = OrderCalculator.Calculate(profile.Profile, lines, discount);
        }
        catch (FinancialRuleException ex)
        {
            return Result<PricedCart>.Fail(ResultStatus.Validation, ex.Message);
        }

        var quoteLines = calculation.Lines
            .Select(l => { var p = products[l.ProductId]; return new QuoteLine(l.LineNo, p.Id, p.Sku, p.Name, p.Version, l); })
            .ToArray();
        var fingerprint = CanonicalJson.Sha256(CartJson(cart));
        var quoteHash = CanonicalJson.Sha256(QuoteFacts(cart, profile, quoteLines, calculation));
        return Result<PricedCart>.Ok(new PricedCart(new CheckoutQuote(cart.OrderType, calculation, quoteLines, discount, fingerprint, quoteHash), profile, products));
    }

    /// <summary>Remaining quantity check for stock-tracked products (no reservation is held).</summary>
    public static async Task<string?> CheckStockAsync(PosDbContext db, PricedCart priced, CancellationToken cancellationToken)
    {
        var needed = priced.Quote.Lines
            .Where(l => priced.Products[l.ProductId].TrackStock)
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Calculation.QuantityQ));
        if (needed.Count == 0)
        {
            return null;
        }

        var ids = needed.Keys.ToArray();
        var balances = await db.Inventory.AsNoTracking().Where(i => ids.Contains(i.ProductId)).ToDictionaryAsync(i => i.ProductId, i => i.QuantityQ, cancellationToken).ConfigureAwait(false);
        foreach (var (productId, q) in needed)
        {
            if (balances.GetValueOrDefault(productId) < q)
            {
                return $"Not enough stock of {priced.Products[productId].Name}.";
            }
        }

        return null;
    }

    public static string CartJson(CartInput cart) =>
        CanonicalJson.Write(w =>
        {
            var discount = cart.Discount ?? OrderDiscount.None;
            w.WriteStartObject();
            w.WriteString("orderType", cart.OrderType.ToString());
            w.WriteStartObject("discount");
            w.WriteString("kind", discount.Kind.ToString());
            w.WriteNumber("value", discount.Value);
            w.WriteEndObject();
            w.WriteStartArray("lines");
            foreach (var l in cart.Lines)
            {
                w.WriteStartObject();
                w.WriteString("productId", l.ProductId.ToString("D"));
                w.WriteNumber("quantityQ", l.QuantityQ);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });

    /// <summary>Stored in CheckoutRequests.QuoteJson: the cart (to re-price) and the frozen quote hash/total.</summary>
    public static string RequestJson(CartInput cart, CheckoutQuote quote) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", "checkout-request.v1");
            w.WritePropertyName("cart");
            w.WriteRawValue(CartJson(cart));
            w.WriteString("quoteHash", quote.QuoteHash);
            w.WriteNumber("totalMinor", quote.Calculation.TotalMinor);
            w.WriteEndObject();
        });

    public static (CartInput Cart, string QuoteHash, long TotalMinor) ReadRequestJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cart = root.GetProperty("cart");
        var discount = cart.GetProperty("discount");
        var lines = cart.GetProperty("lines").EnumerateArray()
            .Select(l => new CartLineInput(Guid.Parse(l.GetProperty("productId").GetString()!), l.GetProperty("quantityQ").GetInt64()))
            .ToArray();
        var input = new CartInput(
            Enum.Parse<OrderType>(cart.GetProperty("orderType").GetString()!),
            lines,
            new OrderDiscount(Enum.Parse<OrderDiscountKind>(discount.GetProperty("kind").GetString()!), discount.GetProperty("value").GetInt64()));
        return (input, root.GetProperty("quoteHash").GetString()!, root.GetProperty("totalMinor").GetInt64());
    }

    private static string QuoteFacts(CartInput cart, EffectiveCalculationProfile profile, IReadOnlyList<QuoteLine> lines, OrderCalculation calc) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("cart");
            w.WriteRawValue(CartJson(cart));
            w.WriteString("policyId", profile.Profile.Id.ToString("D"));
            w.WriteString("policySha256", profile.PayloadSha256);
            w.WriteStartArray("lines");
            foreach (var l in lines)
            {
                var c = l.Calculation;
                w.WriteStartObject();
                w.WriteString("productId", l.ProductId.ToString("D"));
                w.WriteNumber("productVersion", l.ProductVersion);
                w.WriteNumber("unitPriceM", c.UnitPriceMinor);
                w.WriteNumber("quantityQ", c.QuantityQ);
                w.WriteNumber("totalM", c.TotalMinor);
                w.WriteNumber("taxM", c.TaxMinor);
                w.WriteNumber("discountBasisM", c.DiscountBasisMinor);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("totalM", calc.TotalMinor);
            w.WriteEndObject();
        });
}
