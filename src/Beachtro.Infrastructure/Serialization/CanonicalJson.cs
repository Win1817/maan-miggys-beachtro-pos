using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beachtro.Application.Configuration;
using Beachtro.Domain.Financial;

namespace Beachtro.Infrastructure.Serialization;

/// <summary>
/// Canonical serialization (finance design §Immutable transaction snapshot): fixed property order,
/// invariant formatting, integer amounts, UTF-8 without BOM, no whitespace. The same logical
/// content always produces the same bytes and SHA-256.
/// </summary>
public static class CanonicalJson
{
    public static string Write(Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            body(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Sha256(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Versioned canonical payloads stored in PolicyVersions.CanonicalPayloadJson.</summary>
public static class ProfilePayloads
{
    public const string CalculationSchema = "calculation.v1";
    public const string CalendarSchema = "calendar.v1";
    public const string RegistrationSchema = "registration.v1";

    /// <summary>Taxes are ordered by key (ordinal) so input order never changes the hash.</summary>
    public static string Calculation(string strategyKey, Currency currency, IEnumerable<TaxConfigurationDraft> taxes) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", CalculationSchema);
            w.WriteString("strategy", strategyKey);
            w.WriteStartObject("currency");
            w.WriteString("code", currency.Code);
            w.WriteNumber("exponent", currency.Exponent);
            w.WriteEndObject();
            w.WriteStartArray("taxes");
            foreach (var t in taxes.OrderBy(t => t.TaxKey, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("key", t.TaxKey);
                w.WriteNumber("ratePpm", t.RatePpm);
                w.WriteString("priceMode", t.PriceMode.ToString());
                w.WriteString("treatment", t.Treatment.ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });

    public static (string StrategyKey, Currency Currency) ReadCalculation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        RequireSchema(root, CalculationSchema);
        var currency = root.GetProperty("currency");
        return (root.GetProperty("strategy").GetString()!, new Currency(currency.GetProperty("code").GetString()!, currency.GetProperty("exponent").GetInt32()));
    }

    public static string Calendar(string timeZoneId, int cutoffMinute) =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", CalendarSchema);
            w.WriteString("timeZoneId", timeZoneId);
            w.WriteNumber("cutoffMinute", cutoffMinute);
            w.WriteEndObject();
        });

    public static (string TimeZoneId, int CutoffMinute) ReadCalendar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        RequireSchema(root, CalendarSchema);
        return (root.GetProperty("timeZoneId").GetString()!, root.GetProperty("cutoffMinute").GetInt32());
    }

    public static string Registration() =>
        CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", RegistrationSchema);
            w.WriteEndObject();
        });

    private static void RequireSchema(JsonElement root, string expected)
    {
        var schema = root.GetProperty("schema").GetString();
        if (!string.Equals(schema, expected, StringComparison.Ordinal))
        {
            throw new FormatException($"Unsupported payload schema '{schema}', expected '{expected}'.");
        }
    }
}
