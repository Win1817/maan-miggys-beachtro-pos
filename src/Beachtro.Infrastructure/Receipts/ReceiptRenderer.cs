using System.Globalization;
using System.Text.Json;
using Beachtro.Application.Receipts;

namespace Beachtro.Infrastructure.Receipts;

/// <summary>
/// Receipt template v1: 42-column text for 80 mm thermal paper. Reads only the immutable snapshot.
/// Legal invoice wording and required identifiers are pending professional confirmation (D-02), so
/// any receipt whose business profile is not Approved carries a TEST / NOT A TAX INVOICE banner.
/// </summary>
public static class ReceiptRenderer
{
    public const int TemplateVersion = 1;
    public const int Width = 42;
    public const string TestBanner = "*** TEST - NOT A TAX INVOICE ***";
    public const string ReprintBanner = "*** REPRINT ***";

    public static ReceiptDocument RenderSale(string saleSnapshotJson, bool reprint)
    {
        using var doc = JsonDocument.Parse(saleSnapshotJson);
        var s = doc.RootElement;
        var exponent = s.GetProperty("currency").GetProperty("exponent").GetInt32();
        var currency = s.GetProperty("currency").GetProperty("code").GetString()!;
        var business = s.GetProperty("business");
        var isTest = business.GetProperty("approvalState").GetString() != "Approved";
        var lines = new List<string>();

        Header(lines, business, isTest, reprint);
        lines.Add(Pair("Sale", s.GetProperty("transactionNumber").GetString()!));
        lines.Add(Pair("Date", LocalTime(s.GetProperty("completedUtc").GetInt64(), s.GetProperty("timeZoneId").GetString()!)));
        lines.Add(Pair("Cashier", s.GetProperty("cashier").GetProperty("name").GetString()!));
        lines.Add(Pair("Order", s.GetProperty("orderType").GetString() == "DineIn" ? "Dine-in" : "Takeout"));
        lines.Add(Rule());

        var anyInclusive = false;
        var taxTotals = new SortedDictionary<string, (int Rate, long Amount)>(StringComparer.Ordinal);
        foreach (var line in s.GetProperty("lines").EnumerateArray())
        {
            lines.AddRange(Wrap(line.GetProperty("name").GetString()!));
            var qty = Quantity(line.GetProperty("quantityQ").GetInt64());
            lines.Add(Pair($"  {qty} x {Money(line.GetProperty("unitPriceM").GetInt64(), exponent)}", Money(line.GetProperty("priceBasisM").GetInt64(), exponent)));
            anyInclusive |= line.GetProperty("priceMode").GetString() == "Inclusive" && line.GetProperty("treatment").GetString() == "Taxable";
            foreach (var tax in line.GetProperty("taxes").EnumerateArray())
            {
                var key = tax.GetProperty("key").GetString()!;
                var rate = tax.GetProperty("ratePpm").GetInt32();
                var current = taxTotals.GetValueOrDefault(key, (rate, 0));
                taxTotals[key] = (rate, current.Amount + tax.GetProperty("taxM").GetInt64());
            }
        }

        var totals = s.GetProperty("totals");
        lines.Add(Rule());
        lines.Add(Pair("Subtotal", Money(totals.GetProperty("priceBasisSubtotalM").GetInt64(), exponent)));
        var discount = totals.GetProperty("discountBasisM").GetInt64();
        if (discount != 0)
        {
            lines.Add(Pair("Discount", "-" + Money(discount, exponent)));
        }

        foreach (var (key, (rate, amount)) in taxTotals.Where(t => t.Value.Amount != 0))
        {
            lines.Add(Pair($"{(anyInclusive ? "Incl. tax" : "Tax")} {key} {Rate(rate)}", Money(amount, exponent)));
        }

        lines.Add(Pair("TOTAL", $"{currency} {Money(totals.GetProperty("totalM").GetInt64(), exponent)}"));
        var payment = s.GetProperty("payment");
        var method = payment.GetProperty("method").GetString()!;
        if (method == "Cash")
        {
            lines.Add(Pair("Cash", Money(payment.GetProperty("tenderedM").GetInt64(), exponent)));
            lines.Add(Pair("Change", Money(payment.GetProperty("changeM").GetInt64(), exponent)));
        }
        else
        {
            lines.Add(Pair("GCash", Money(payment.GetProperty("appliedM").GetInt64(), exponent)));
            if (payment.GetProperty("reference").ValueKind == JsonValueKind.String)
            {
                lines.Add(Pair("GCash ref", payment.GetProperty("reference").GetString()!));
            }
        }

        Footer(lines, isTest);
        return new ReceiptDocument("Sale", Guid.Parse(s.GetProperty("saleId").GetString()!), s.GetProperty("transactionNumber").GetString()!,
            TemplateVersion, Width, isTest, reprint, lines);
    }

    public static ReceiptDocument RenderCorrection(string correctionSnapshotJson, string originalSaleSnapshotJson, bool reprint)
    {
        using var doc = JsonDocument.Parse(correctionSnapshotJson);
        using var original = JsonDocument.Parse(originalSaleSnapshotJson);
        var c = doc.RootElement;
        var o = original.RootElement;
        var exponent = o.GetProperty("currency").GetProperty("exponent").GetInt32();
        var currency = o.GetProperty("currency").GetProperty("code").GetString()!;
        var business = o.GetProperty("business");
        var isTest = business.GetProperty("approvalState").GetString() != "Approved";
        var lines = new List<string>();

        Header(lines, business, isTest, reprint);
        lines.Add(Center(c.GetProperty("kind").GetString() == "Void" ? "VOID" : "REFUND"));
        lines.Add(Pair("Ref", Abbreviate(c.GetProperty("correctionNumber").GetString()!)));
        lines.Add(Pair("Original sale", c.GetProperty("originalTransactionNumber").GetString()!));
        lines.Add(Pair("Date", LocalTime(c.GetProperty("createdUtc").GetInt64(), c.GetProperty("timeZoneId").GetString()!)));
        lines.Add(Pair("Approved by", c.GetProperty("approver").GetProperty("name").GetString()!));
        lines.Add(Rule());
        foreach (var line in c.GetProperty("lines").EnumerateArray())
        {
            lines.AddRange(Wrap(line.GetProperty("name").GetString()!));
            lines.Add(Pair($"  {Quantity(line.GetProperty("quantityQ").GetInt64())} returned", "-" + Money(line.GetProperty("totalM").GetInt64(), exponent)));
        }

        lines.Add(Rule());
        var totals = c.GetProperty("totals");
        var tax = totals.GetProperty("taxM").GetInt64();
        if (tax != 0)
        {
            lines.Add(Pair("Tax reversed", "-" + Money(tax, exponent)));
        }

        lines.Add(Pair("TOTAL REFUND", $"{currency} {Money(totals.GetProperty("totalM").GetInt64(), exponent)}"));
        var refund = c.GetProperty("refund");
        lines.Add(Pair("Refunded by", refund.GetProperty("method").GetString()!));
        lines.AddRange(Wrap("Reason: " + c.GetProperty("reason").GetString()));
        Footer(lines, isTest);
        return new ReceiptDocument("Correction", Guid.Parse(c.GetProperty("correctionId").GetString()!), c.GetProperty("correctionNumber").GetString()!,
            TemplateVersion, Width, isTest, reprint, lines);
    }

    /// <summary>A test page proving the printer and template, with no financial content.</summary>
    public static ReceiptDocument RenderTestPage(string printerName, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            Center("PRINTER TEST"),
            Center(TestBanner),
            Rule(),
            Pair("Printer", printerName.Length > 26 ? printerName[..26] : printerName),
            Pair("Printed", now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            Pair("Template", $"v{TemplateVersion}, {Width} columns"),
            Rule(),
            "0123456789012345678901234567890123456789AB",
            Center("Not a sale. No amounts recorded."),
        };
        return new ReceiptDocument("Test", Guid.Empty, "TEST", TemplateVersion, Width, true, false, lines);
    }

    public static string Money(long minor, int exponent)
    {
        var value = minor / (decimal)Pow10(exponent); // display only; amounts are already exact integers
        return value.ToString("#,##0." + new string('0', exponent), CultureInfo.InvariantCulture).TrimEnd('.');
    }

    public static string Quantity(long quantityQ) =>
        quantityQ % 1000 == 0
            ? (quantityQ / 1000).ToString(CultureInfo.InvariantCulture)
            : (quantityQ / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    private static void Header(List<string> lines, JsonElement business, bool isTest, bool reprint)
    {
        lines.AddRange(Wrap(business.GetProperty("registeredName").GetString()!).Select(Center));
        lines.AddRange(Wrap(business.GetProperty("address").GetString()!).Select(Center));
        if (business.GetProperty("contact").ValueKind == JsonValueKind.String)
        {
            lines.Add(Center(business.GetProperty("contact").GetString()!));
        }

        if (business.GetProperty("taxIdentifier").ValueKind == JsonValueKind.String)
        {
            lines.Add(Center("TIN " + business.GetProperty("taxIdentifier").GetString()));
        }

        if (isTest)
        {
            lines.Add(Center(TestBanner));
        }

        if (reprint)
        {
            lines.Add(Center(ReprintBanner));
        }

        lines.Add(Rule());
    }

    private static void Footer(List<string> lines, bool isTest)
    {
        lines.Add(Rule());
        lines.Add(Center("Thank you!"));
        if (isTest)
        {
            lines.Add(Center(TestBanner));
        }
    }

    private static string LocalTime(long utcMs, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMs), zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Rate(int ppm) => (ppm / 10_000m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Abbreviate(string number) => number.Length > 20 ? number[..20] + "…" : number;

    private static string Rule() => new('-', Width);

    private static string Center(string text)
    {
        var t = text.Length > Width ? text[..Width] : text;
        return t.PadLeft((Width + t.Length) / 2).PadRight(Width).TrimEnd();
    }

    private static string Pair(string left, string right)
    {
        var space = Width - left.Length - right.Length;
        if (space >= 1)
        {
            return left + new string(' ', space) + right;
        }

        var maxLeft = Math.Max(1, Width - right.Length - 1);
        return left[..Math.Min(left.Length, maxLeft)] + " " + right;
    }

    private static IEnumerable<string> Wrap(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = string.Empty;
        foreach (var word in words)
        {
            var w = word.Length > Width ? word[..Width] : word;
            if (line.Length == 0)
            {
                line = w;
            }
            else if (line.Length + 1 + w.Length <= Width)
            {
                line += " " + w;
            }
            else
            {
                yield return line;
                line = w;
            }
        }

        if (line.Length > 0)
        {
            yield return line;
        }
    }

    private static long Pow10(int exponent)
    {
        long p = 1;
        for (var i = 0; i < exponent; i++)
        {
            p *= 10;
        }

        return p;
    }
}
