using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SkiaSharp;
using Weighbridge.Core.Units;
using Weighbridge.Reporting.Text;

namespace Weighbridge.Reporting.Reports;

public enum ColumnKind { Text, Weight, Integer, Decimal, DateTime }

public sealed record ReportColumn(string Header, ColumnKind Kind = ColumnKind.Text, float Width = 1f);

/// <summary>A rectangular report: what every exporter (CSV, XLSX, PDF, screen) consumes.</summary>
public sealed record ReportTable
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? CompanyName { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GeneratedBy { get; init; }
    public required IReadOnlyList<ReportColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public IReadOnlyList<object?>? Totals { get; init; }
    public WeightDisplayOptions Display { get; init; } = WeightDisplayOptions.DefaultKg;
    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm";
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public bool Landscape { get; init; }
}

public static class ReportFormat
{
    public static string Cell(object? value, ColumnKind kind, ReportTable t, bool forDisplay = true) => value switch
    {
        null => string.Empty,
        Weight w => forDisplay ? t.Display.FormatNumber(w) : decimal.Round(w.In(t.Display.Unit), t.Display.Decimals, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture),
        DateTimeOffset d => TimeZoneInfo.ConvertTime(d, t.TimeZone).ToString(forDisplay ? t.DateFormat : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal m => forDisplay ? m.ToString("#,0.##", CultureInfo.InvariantCulture) : m.ToString(CultureInfo.InvariantCulture),
        long or int => forDisplay && kind != ColumnKind.Text ? Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("#,0", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)!,
        bool b => b ? "Yes" : "No",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    public static bool RightAligned(ColumnKind kind) => kind is ColumnKind.Weight or ColumnKind.Integer or ColumnKind.Decimal;
}

public static class CsvExporter
{
    /// <summary>RFC 4180 CSV, UTF-8 with BOM (opens correctly in Excel, including Sinhala/Tamil). Text cells that
    /// Excel would treat as formulas are prefixed with an apostrophe (CSV injection protection).</summary>
    public static void Write(Stream stream, ReportTable t)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true) { NewLine = "\r\n" };
        w.WriteLine(string.Join(',', t.Columns.Select(c => Escape(c.Kind == ColumnKind.Weight ? $"{c.Header} ({WeightUnits.Symbol(t.Display.Unit)})" : c.Header, guard: true))));
        foreach (var row in t.Rows) w.WriteLine(Line(row, t));
        if (t.Totals is { } totals) w.WriteLine(Line(totals, t));
    }

    public static byte[] ToBytes(ReportTable t)
    {
        using var ms = new MemoryStream();
        Write(ms, t);
        return ms.ToArray();
    }

    private static string Line(IReadOnlyList<object?> row, ReportTable t) =>
        string.Join(',', t.Columns.Select((c, i) => Escape(ReportFormat.Cell(i < row.Count ? row[i] : null, c.Kind, t, forDisplay: false), guard: c.Kind == ColumnKind.Text)));

    internal static string Escape(string value, bool guard)
    {
        if (guard && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

public static class XlsxExporter
{
    public static void Write(Stream stream, ReportTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheetName(t.Title));
        var row = 1;
        ws.Cell(row, 1).Value = t.Title;
        ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(14);
        row++;
        if (!string.IsNullOrWhiteSpace(t.CompanyName)) ws.Cell(row++, 1).Value = t.CompanyName;
        if (!string.IsNullOrWhiteSpace(t.Subtitle)) ws.Cell(row++, 1).Value = t.Subtitle;
        ws.Cell(row++, 1).Value = $"Generated {TimeZoneInfo.ConvertTime(t.GeneratedAt, t.TimeZone):yyyy-MM-dd HH:mm}{(t.GeneratedBy is null ? "" : " by " + t.GeneratedBy)}";
        row++;

        var headerRow = row;
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var col = t.Columns[c];
            var cell = ws.Cell(row, c + 1);
            cell.Value = col.Kind == ColumnKind.Weight ? $"{col.Header} ({WeightUnits.Symbol(t.Display.Unit)})" : col.Header;
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DCE6F1")).Border.SetBottomBorder(XLBorderStyleValues.Thin);
        }
        row++;
        var weightFormat = t.Display.Decimals > 0 ? "#,##0." + new string('0', t.Display.Decimals) : "#,##0";

        void WriteRow(IReadOnlyList<object?> values, bool bold)
        {
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var v = c < values.Count ? values[c] : null;
                var cell = ws.Cell(row, c + 1);
                switch (v)
                {
                    case null:
                        break;
                    case Weight w:
                        cell.Value = decimal.Round(w.In(t.Display.Unit), Math.Max(t.Display.Decimals, 3), MidpointRounding.AwayFromZero);
                        cell.Style.NumberFormat.Format = weightFormat;
                        break;
                    case DateTimeOffset d:
                        cell.Value = TimeZoneInfo.ConvertTime(d, t.TimeZone).DateTime;
                        cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                        break;
                    case long or int:
                        cell.Value = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                        cell.Style.NumberFormat.Format = "#,##0";
                        break;
                    case decimal m:
                        cell.Value = m;
                        cell.Style.NumberFormat.Format = "#,##0.##";
                        break;
                    case bool b:
                        cell.Value = b ? "Yes" : "No";
                        break;
                    default:
                        // Strings are written as text values, never as formulas.
                        cell.Value = Convert.ToString(v, CultureInfo.InvariantCulture);
                        break;
                }
                if (bold) cell.Style.Font.SetBold();
            }
            row++;
        }

        foreach (var r in t.Rows) WriteRow(r, bold: false);
        var lastDataRow = row - 1;
        if (t.Totals is { } totals)
        {
            WriteRow(totals, bold: true);
            ws.Row(row - 1).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
        }

        if (t.Rows.Count > 0) ws.Range(headerRow, 1, lastDataRow, t.Columns.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns(1, t.Columns.Count).AdjustToContents(headerRow, Math.Min(row, headerRow + 500));
        foreach (var column in ws.Columns(1, t.Columns.Count)) if (column.Width > 60) column.Width = 60;
        ws.PageSetup.PageOrientation = t.Landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        wb.SaveAs(stream);
    }

    public static byte[] ToBytes(ReportTable t)
    {
        using var ms = new MemoryStream();
        Write(ms, t);
        return ms.ToArray();
    }

    private static string SafeSheetName(string title)
    {
        var name = new string(title.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();
        if (name.Length == 0) name = "Report";
        return name.Length > 31 ? name[..31] : name;
    }
}

public static class PdfReportRenderer
{
    public static byte[] Render(ReportTable t)
    {
        var (pw, ph) = t.Landscape || t.Columns.Count > 8 ? (841.89f, 595.28f) : (595.28f, 841.89f);
        const float margin = 30f;
        var text = TextEngine.Shared;
        var cellStyle = new TextStyle(t.Columns.Count > 10 ? 6.6f : 7.6f);
        var headStyle = cellStyle with { Bold = true };
        var rowH = TextEngine.Metrics(cellStyle).LineHeight + 3;
        var contentW = pw - 2 * margin;
        var totalWeight = t.Columns.Sum(c => c.Width);
        var widths = t.Columns.Select(c => contentW * c.Width / totalWeight).ToArray();

        // First pass: how many rows fit per page (the first page has the title block).
        const float titleBlock = 64f;
        const float footer = 24f;
        var firstCapacity = (int)((ph - 2 * margin - titleBlock - footer - rowH) / rowH);
        var capacity = (int)((ph - 2 * margin - footer - rowH) / rowH);
        var rows = t.Rows.ToList();
        var pages = new List<List<IReadOnlyList<object?>>>();
        var index = 0;
        do
        {
            var take = Math.Max(1, pages.Count == 0 ? firstCapacity : capacity);
            pages.Add(rows.Skip(index).Take(take).ToList());
            index += take;
        }
        while (index < rows.Count);

        using var ms = new MemoryStream();
        using (var stream = new SKManagedWStream(ms))
        using (var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = t.Title, Creator = "Advanced Weighbridge Management System", Creation = DateTime.UtcNow }))
        {
            for (var p = 0; p < pages.Count; p++)
            {
                var canvas = doc.BeginPage(pw, ph);
                var y = margin;
                if (p == 0)
                {
                    text.Draw(canvas, t.Title, margin, y + 14, new TextStyle(14, Bold: true));
                    if (!string.IsNullOrWhiteSpace(t.CompanyName)) text.Draw(canvas, t.CompanyName, pw - margin, y + 14, new TextStyle(10, Bold: true), TextAlign.Right);
                    y += 22;
                    if (!string.IsNullOrWhiteSpace(t.Subtitle)) text.Draw(canvas, text.Ellipsize(t.Subtitle, contentW, new TextStyle(8.5f)), margin, y + 9, new TextStyle(8.5f));
                    y += 14;
                    text.Draw(canvas, $"Generated {TimeZoneInfo.ConvertTime(t.GeneratedAt, t.TimeZone):yyyy-MM-dd HH:mm}{(t.GeneratedBy is null ? "" : " by " + t.GeneratedBy)} · {t.Rows.Count:#,0} rows",
                        margin, y + 9, new TextStyle(7.5f, Color: SKColors.DimGray));
                    y = margin + titleBlock;
                }

                // Header row (repeated on every page).
                using (var fill = new SKPaint { Color = new SKColor(0xDC, 0xE6, 0xF1) }) canvas.DrawRect(margin, y, contentW, rowH, fill);
                DrawRow(canvas, t, widths, margin, y, rowH, t.Columns.Select(c => (object?)(c.Kind == ColumnKind.Weight ? $"{c.Header} ({WeightUnits.Symbol(t.Display.Unit)})" : c.Header)).ToList(), headStyle, header: true);
                y += rowH;

                for (var r = 0; r < pages[p].Count; r++)
                {
                    if (r % 2 == 1)
                    {
                        using var zebra = new SKPaint { Color = new SKColor(0xF5, 0xF7, 0xFA) };
                        canvas.DrawRect(margin, y, contentW, rowH, zebra);
                    }
                    DrawRow(canvas, t, widths, margin, y, rowH, pages[p][r], cellStyle, header: false);
                    y += rowH;
                }

                if (p == pages.Count - 1 && t.Totals is { } totals)
                {
                    using var line = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke };
                    canvas.DrawLine(margin, y, margin + contentW, y, line);
                    DrawRow(canvas, t, widths, margin, y, rowH, totals, headStyle, header: false);
                }

                text.Draw(canvas, $"Page {p + 1} of {pages.Count}", pw - margin, ph - margin + 10, new TextStyle(7.5f, Color: SKColors.DimGray), TextAlign.Right);
                text.Draw(canvas, t.Title, margin, ph - margin + 10, new TextStyle(7.5f, Color: SKColors.DimGray));
                doc.EndPage();
            }
            doc.Close();
        }
        return ms.ToArray();
    }

    private static void DrawRow(SKCanvas canvas, ReportTable t, float[] widths, float x, float y, float rowH, IReadOnlyList<object?> values, TextStyle style, bool header)
    {
        var text = TextEngine.Shared;
        var baseline = y + rowH - 4;
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var kind = t.Columns[c].Kind;
            var raw = c < values.Count ? values[c] : null;
            var s = header ? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty : ReportFormat.Cell(raw, kind, t);
            s = text.Ellipsize(s, widths[c] - 6, style);
            var right = !header && ReportFormat.RightAligned(kind) || header && ReportFormat.RightAligned(kind);
            text.Draw(canvas, s, right ? x + widths[c] - 3 : x + 3, baseline, style, right ? TextAlign.Right : TextAlign.Left);
            x += widths[c];
        }
    }
}
