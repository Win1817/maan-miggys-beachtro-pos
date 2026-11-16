using System.Globalization;
using System.Text;
using Beachtro.Application.Reports;

namespace Beachtro.Infrastructure.Reports;

/// <summary>Run facts printed on every output. No user name: outputs depend only on stored rows.</summary>
public sealed record ReportRunHeader(int Revision, string GeneratedLocal, string WatermarkSha256, bool IsBusinessClose);

/// <summary>
/// Fixed-width text layout of a report. The screen preview and the PDF both use these lines, so
/// they always show the same figures as the CSV (all three read the same stored snapshot).
/// </summary>
public static class ReportText
{
    public const string ColumnGap = "  ";
    private const int NoteWidth = 130;

    public static IReadOnlyList<string> Lines(ReportDocument d, ReportRunHeader run)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(run);
        var lines = new List<string>
        {
            d.Title + (run.IsBusinessClose ? " (business day close)" : string.Empty),
            $"Business dates {d.StartDate} to {ReportBuilder.LastDate(d.EndExclusiveDate)} | {d.TimeZoneId} | {d.CurrencyCode}",
            $"Revision {run.Revision} | generated {run.GeneratedLocal} | data watermark {run.WatermarkSha256[..12]}",
        };
        if (d.IsTest)
        {
            lines.Add("*** TEST - SYNTHETIC DATA - NOT FOR TAX FILING ***");
        }

        lines.Add(string.Empty);
        foreach (var note in d.Notes)
        {
            lines.AddRange(Wrap(note, NoteWidth));
        }

        if (d.Metrics.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Summary");
            foreach (var m in d.Metrics)
            {
                lines.Add("  " + m.Label.PadRight(30) + ReportValues.Display(m.Kind, m.Value, d.CurrencyExponent).PadLeft(18));
            }
        }

        foreach (var t in d.Tables)
        {
            lines.Add(string.Empty);
            lines.Add(t.Title);
            var widths = t.Columns.Select((c, i) => Math.Max(c.Width, Math.Max(c.Header.Length, MaxValueWidth(t, i, d.CurrencyExponent)))).ToArray();
            lines.Add(Row(t.Columns.Select(c => c.Header).ToArray(), t, widths, d.CurrencyExponent, header: true));
            var rule = new string('-', widths.Sum() + (ColumnGap.Length * (widths.Length - 1)));
            lines.Add(rule);
            if (t.Rows.Count == 0)
            {
                lines.Add("(no data in this period)");
            }

            lines.AddRange(t.Rows.Select(r => Row(r, t, widths, d.CurrencyExponent, header: false)));
            if (t.Totals is { } totals)
            {
                lines.Add(rule);
                lines.Add(Row(totals, t, widths, d.CurrencyExponent, header: false));
            }
        }

        return lines;
    }

    private static int MaxValueWidth(ReportTable t, int column, int exponent)
    {
        var kind = t.Columns[column].Kind;
        if (kind == ReportCellKind.Text)
        {
            return 0; // text is truncated to the column width
        }

        var values = t.Rows.Select(r => r[column]);
        if (t.Totals is { } totals)
        {
            values = values.Append(totals[column]);
        }

        return values.Select(v => ReportValues.Display(kind, v, exponent).Length).DefaultIfEmpty(0).Max();
    }

    private static string Row(IReadOnlyList<string> cells, ReportTable t, int[] widths, int exponent, bool header)
    {
        var parts = new string[widths.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            var kind = t.Columns[i].Kind;
            var text = header ? cells[i] : ReportValues.Display(kind, cells[i], exponent);
            parts[i] = kind == ReportCellKind.Text || (header && i == 0)
                ? Fit(text, widths[i]).PadRight(widths[i])
                : text.PadLeft(widths[i]);
        }

        return string.Join(ColumnGap, parts).TrimEnd();
    }

    private static string Fit(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "~";

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : string.Empty).Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}

/// <summary>
/// CSV export (T-12): UTF-8 with BOM, CRLF, every field quoted with doubled quotes, invariant
/// amounts without thousands separators, and text that a spreadsheet could run as a formula
/// prefixed with an apostrophe. Numbers are never prefixed, so negative amounts stay numeric.
/// </summary>
public static class ReportCsv
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r', '\n'];

    public static byte[] Render(ReportDocument d, ReportRunHeader run)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(run);
        var sb = new StringBuilder();
        void Line(params string[] fields) => sb.Append(string.Join(',', fields.Select(Quote))).Append("\r\n");

        Line("Report", Text(d.Title));
        Line("First business date", d.StartDate);
        Line("Last business date", ReportBuilder.LastDate(d.EndExclusiveDate));
        Line("Time zone", d.TimeZoneId);
        Line("Currency", d.CurrencyCode);
        Line("Revision", run.Revision.ToString(CultureInfo.InvariantCulture));
        Line("Generated", run.GeneratedLocal);
        Line("Data watermark", run.WatermarkSha256);
        Line("Business day close", run.IsBusinessClose ? "Yes" : "No");
        Line("Test data", d.IsTest ? "Yes - synthetic, not for tax filing" : "No");
        foreach (var note in d.Notes)
        {
            Line("Note", Text(note));
        }

        if (d.Metrics.Count > 0)
        {
            sb.Append("\r\n");
            Line("Summary", string.Empty);
            foreach (var m in d.Metrics)
            {
                Line(Text(m.Label.Trim()), Cell(m.Kind, m.Value, d.CurrencyExponent));
            }
        }

        foreach (var t in d.Tables)
        {
            sb.Append("\r\n");
            Line(Text(t.Title));
            Line(t.Columns.Select(c => Text(c.Header)).ToArray());
            foreach (var row in t.Rows)
            {
                Line(row.Select((v, i) => Cell(t.Columns[i].Kind, v, d.CurrencyExponent)).ToArray());
            }

            if (t.Totals is { } totals)
            {
                Line(totals.Select((v, i) => i == 0 ? Text(v) : Cell(t.Columns[i].Kind, v, d.CurrencyExponent)).ToArray());
            }
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = new UTF8Encoding(false).GetBytes(sb.ToString());
        return [.. preamble, .. body];
    }

    /// <summary>Neutralizes text a spreadsheet would treat as a formula.</summary>
    public static string Text(string value) =>
        value.Length > 0 && Array.IndexOf(FormulaStarts, value[0]) >= 0 ? "'" + value : value;

    private static string Cell(ReportCellKind kind, string stored, int exponent) =>
        kind == ReportCellKind.Text ? Text(stored) : ReportValues.Plain(kind, stored, exponent);

    private static string Quote(string field) => "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// PDF export: the report text on landscape A4 pages in the standard Courier font, with a footer
/// on each page. Built by <see cref="SimplePdf"/>, which writes no dates or random IDs.
/// </summary>
public static class ReportPdf
{
    private const int LinesPerPage = 46;

    public static byte[] Render(ReportDocument d, ReportRunHeader run)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(run);
        var lines = ReportText.Lines(d, run);
        var pages = lines.Skip(1).Chunk(LinesPerPage).ToArray();
        if (pages.Length == 0)
        {
            pages = [[]];
        }

        var widest = lines.Max(l => l.Length);
        var fontSize = Math.Min(8.0, (SimplePdf.PageWidth - (2 * SimplePdf.Margin)) / (0.6 * Math.Max(widest, 1)));
        var pdf = new SimplePdf();
        for (var i = 0; i < pages.Length; i++)
        {
            var footer = $"{d.Title} | revision {run.Revision} | page {i + 1} of {pages.Length}" + (d.IsTest ? " | TEST - NOT FOR TAX FILING" : string.Empty);
            pdf.AddPage(lines[0], pages[i], footer, fontSize);
        }

        return pdf.ToBytes();
    }
}

/// <summary>
/// Minimal PDF 1.4 writer for text pages: Helvetica-Bold title, Courier body, Courier footer.
/// Characters outside Windows-1252 print as '?'. Output is a pure function of its input.
/// </summary>
public sealed class SimplePdf
{
    public const double PageWidth = 842;
    public const double PageHeight = 595;
    public const double Margin = 36;

    private readonly List<string> _contents = [];

    public void AddPage(string title, IReadOnlyList<string> body, string footer, double fontSize)
    {
        var leading = Math.Round(fontSize * 1.25, 2);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"BT /F2 13 Tf {N(Margin)} {N(PageHeight - Margin - 6)} Td ({Escape(title)}) Tj ET\n");
        sb.Append(CultureInfo.InvariantCulture, $"BT /F1 {N(fontSize)} Tf {N(leading)} TL {N(Margin)} {N(PageHeight - Margin - 26)} Td\n");
        foreach (var line in body)
        {
            sb.Append('(').Append(Escape(line)).Append(") Tj T*\n");
        }

        sb.Append("ET\n");
        sb.Append(CultureInfo.InvariantCulture, $"BT /F1 7 Tf {N(Margin)} {N(Margin - 12)} Td ({Escape(footer)}) Tj ET\n");
        _contents.Add(sb.ToString());
    }

    public byte[] ToBytes()
    {
        // Objects: 1 catalog, 2 pages, 3 Courier, 4 Helvetica-Bold, then (page, content) pairs.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(' ', _contents.Select((_, i) => $"{5 + (2 * i)} 0 R")) + $"] /Count {_contents.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };
        for (var i = 0; i < _contents.Count; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + (2 * i)} 0 R >>");
            var content = _contents[i];
            objects.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        using var output = new MemoryStream();
        void Write(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            output.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = output.Position;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>PDF string escaping; non-ASCII as octal escapes in Windows-1252.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var code = WinAnsi(ch);
            if (code is (byte)'(' or (byte)')' or (byte)'\\')
            {
                sb.Append('\\').Append((char)code);
            }
            else if (code is < 0x20 or > 0x7E)
            {
                sb.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
            }
            else
            {
                sb.Append((char)code);
            }
        }

        return sb.ToString();
    }

    private static byte WinAnsi(char ch) => ch switch
    {
        >= ' ' and <= '~' => (byte)ch,
        >= ' ' and <= 'ÿ' => (byte)ch,
        '€' => 0x80,
        '…' => 0x85,
        '‘' => 0x91,
        '’' => 0x92,
        '“' => 0x93,
        '”' => 0x94,
        '•' => 0x95,
        '–' => 0x96,
        '—' => 0x97,
        _ => (byte)'?',
    };

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
