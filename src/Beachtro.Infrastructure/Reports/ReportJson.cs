using System.Globalization;
using System.Text.Json;
using Beachtro.Application.Reports;
using Beachtro.Domain.Model;
using Beachtro.Infrastructure.Serialization;

namespace Beachtro.Infrastructure.Reports;

/// <summary>
/// Canonical report.v1 snapshot stored in ReportRuns.SnapshotJson. Every export is rendered from
/// this text, never from a fresh query, so a run always re-renders to the same bytes.
/// </summary>
public static class ReportJson
{
    public const string Schema = "report.v1";

    public static string Write(ReportDocument d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return CanonicalJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("type", d.Type.ToString());
            w.WriteString("title", d.Title);
            w.WriteString("startDate", d.StartDate);
            w.WriteString("endExclusiveDate", d.EndExclusiveDate);
            w.WriteString("currencyCode", d.CurrencyCode);
            w.WriteNumber("currencyExponent", d.CurrencyExponent);
            w.WriteString("timeZoneId", d.TimeZoneId);
            w.WriteBoolean("isTest", d.IsTest);
            w.WriteStartArray("notes");
            foreach (var note in d.Notes)
            {
                w.WriteStringValue(note);
            }

            w.WriteEndArray();
            w.WriteStartArray("metrics");
            foreach (var m in d.Metrics)
            {
                w.WriteStartObject();
                w.WriteString("label", m.Label);
                w.WriteString("kind", m.Kind.ToString());
                w.WriteString("value", m.Value);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("tables");
            foreach (var t in d.Tables)
            {
                w.WriteStartObject();
                w.WriteString("title", t.Title);
                w.WriteStartArray("columns");
                foreach (var c in t.Columns)
                {
                    w.WriteStartObject();
                    w.WriteString("header", c.Header);
                    w.WriteString("kind", c.Kind.ToString());
                    w.WriteNumber("width", c.Width);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (var row in t.Rows)
                {
                    WriteRow(w, row);
                }

                w.WriteEndArray();
                if (t.Totals is { } totals)
                {
                    w.WritePropertyName("totals");
                    WriteRow(w, totals);
                }
                else
                {
                    w.WriteNull("totals");
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    public static ReportDocument Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.GetProperty("schema").GetString() != Schema)
        {
            throw new FormatException("Unsupported report snapshot schema.");
        }

        var tables = r.GetProperty("tables").EnumerateArray().Select(t => new ReportTable(
            t.GetProperty("title").GetString()!,
            t.GetProperty("columns").EnumerateArray().Select(c => new ReportColumn(
                c.GetProperty("header").GetString()!,
                Enum.Parse<ReportCellKind>(c.GetProperty("kind").GetString()!),
                c.GetProperty("width").GetInt32())).ToArray(),
            t.GetProperty("rows").EnumerateArray().Select(ReadRow).ToArray(),
            t.GetProperty("totals").ValueKind == JsonValueKind.Null ? null : ReadRow(t.GetProperty("totals")))).ToArray();

        return new ReportDocument(
            Enum.Parse<ReportType>(r.GetProperty("type").GetString()!),
            r.GetProperty("title").GetString()!,
            r.GetProperty("startDate").GetString()!,
            r.GetProperty("endExclusiveDate").GetString()!,
            r.GetProperty("currencyCode").GetString()!,
            r.GetProperty("currencyExponent").GetInt32(),
            r.GetProperty("timeZoneId").GetString()!,
            r.GetProperty("isTest").GetBoolean(),
            r.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToArray(),
            r.GetProperty("metrics").EnumerateArray().Select(m => new ReportMetric(
                m.GetProperty("label").GetString()!,
                Enum.Parse<ReportCellKind>(m.GetProperty("kind").GetString()!),
                m.GetProperty("value").GetString()!)).ToArray(),
            tables);
    }

    private static void WriteRow(Utf8JsonWriter w, IReadOnlyList<string> row)
    {
        w.WriteStartArray();
        foreach (var cell in row)
        {
            w.WriteStringValue(cell);
        }

        w.WriteEndArray();
    }

    private static string[] ReadRow(JsonElement row) => row.EnumerateArray().Select(c => c.GetString()!).ToArray();
}

/// <summary>Invariant text for stored cell values. Amounts stay exact integers until display.</summary>
public static class ReportValues
{
    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Display form: thousands separators, fixed decimals, leading minus.</summary>
    public static string Display(ReportCellKind kind, string stored, int exponent) => kind switch
    {
        _ when stored.Length == 0 => string.Empty,
        ReportCellKind.Money => Decimal(long.Parse(stored, CultureInfo.InvariantCulture), exponent, thousands: true),
        ReportCellKind.Quantity => Quantity(long.Parse(stored, CultureInfo.InvariantCulture)),
        _ => stored,
    };

    /// <summary>Machine form for CSV: no thousands separators, '.' decimal point.</summary>
    public static string Plain(ReportCellKind kind, string stored, int exponent) => kind switch
    {
        _ when stored.Length == 0 => string.Empty,
        ReportCellKind.Money => Decimal(long.Parse(stored, CultureInfo.InvariantCulture), exponent, thousands: false),
        ReportCellKind.Quantity => Quantity(long.Parse(stored, CultureInfo.InvariantCulture)),
        _ => stored,
    };

    public static string Quantity(long quantityQ) =>
        quantityQ % 1000 == 0
            ? (quantityQ / 1000).ToString(CultureInfo.InvariantCulture)
            : (quantityQ / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Exact: built from the integer digits, no floating point.</summary>
    public static string Decimal(long minor, int exponent, bool thousands)
    {
        var negative = minor < 0;
        var digits = ((Int128)minor < 0 ? -(Int128)minor : minor).ToString(CultureInfo.InvariantCulture).PadLeft(exponent + 1, '0');
        var whole = digits[..^exponent];
        var fraction = exponent == 0 ? string.Empty : "." + digits[^exponent..];
        if (thousands)
        {
            whole = long.Parse(whole, CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture);
        }

        return (negative ? "-" : string.Empty) + whole + fraction;
    }
}
