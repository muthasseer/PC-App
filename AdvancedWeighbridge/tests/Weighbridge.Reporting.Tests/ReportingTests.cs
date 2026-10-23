using System.Diagnostics;
using System.Text;
using ClosedXML.Excel;
using SkiaSharp;
using Weighbridge.Core.Units;
using Weighbridge.Reporting.Printing;
using Weighbridge.Reporting.Reports;
using Weighbridge.Reporting.Text;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Reporting.Tests;

public static class Samples
{
    public static readonly byte[] Key = Encoding.UTF8.GetBytes("unit-test-ticket-signing-key-0001");

    public static TicketData Ticket(TicketStatus status = TicketStatus.Completed, bool manual = false)
    {
        var uid = Guid.Parse("6f1c2a1e-0d4b-4c8a-9a77-2c0b1f0e5a11");
        var t0 = new DateTimeOffset(2026, 3, 1, 8, 0, 12, TimeSpan.Zero);
        return new TicketData
        {
            Company = new CompanyInfo { Name = "Lanka Aggregates (Pvt) Ltd", Address = "No. 12, Quarry Road, Avissawella", Phone = "+94 36 222 1234", Email = "info@example.lk", TaxNo = "114567890-7000" },
            TicketNo = "CMB-2026-000042",
            TransactionUid = uid,
            Status = status,
            SiteName = "Colombo Yard",
            WeighbridgeName = "WB1",
            VehicleReg = "WP CAB-1234",
            DriverName = "S. Perera",
            Customer = "Ceylon Builders",
            Supplier = null,
            Material = "Metal 3/4\" (ගල් 3/4)",
            OrderNo = "SO-7781",
            DeliveryNote = "DN-5521",
            Direction = "Outbound",
            First = new TicketWeighing(Weight.FromKilograms(12300), t0, manual ? "Manual entry" : "Indicator, stable", manual, true),
            Second = status == TicketStatus.Pending ? null : new TicketWeighing(Weight.FromKilograms(32500), t0.AddMinutes(38), "Indicator, stable", false, true),
            Gross = status == TicketStatus.Pending ? null : Weight.FromKilograms(32500),
            Tare = status == TicketStatus.Pending ? null : Weight.FromKilograms(12300),
            Net = status == TicketStatus.Pending ? null : Weight.FromKilograms(20200),
            TareMethodLabel = "measured",
            OperatorName = "K. Silva",
            ModeLabel = "Semi-automatic",
            HasManualWeight = manual,
            CancelReason = status == TicketStatus.Cancelled ? "Duplicate entry" : null,
            PrintedAt = t0.AddMinutes(39),
            VerificationPayload = TicketVerification.CreatePayload(Key, "CMB-2026-000042", uid, 20_200_000),
        };
    }
}

public class TextEngineTests
{
    [Fact]
    public void Segments_mixed_scripts()
    {
        var runs = TextEngine.Segment("Net / ශුද්ධ බර 20,200 kg / நிகர எடை");
        Assert.Equal([Script.Latin, Script.Sinhala, Script.Latin, Script.Tamil], runs.Select(r => r.Script));
        Assert.Equal("Net weight 1".Length, TextEngine.Segment("Net weight 1")[0].Text.Length);
    }

    [Fact]
    public void Measures_and_wraps()
    {
        var engine = TextEngine.Shared;
        var style = new TextStyle(10);
        Assert.True(engine.Measure("ශුද්ධ බර", style) > 10);
        Assert.True(engine.Measure("WWWW", style) > engine.Measure("iiii", style));
        var lines = engine.Wrap("The quick brown fox jumps over the lazy dog", 60, style);
        Assert.True(lines.Count > 2);
        Assert.All(lines, l => Assert.True(engine.Measure(l, style) <= 60.5f));
        Assert.EndsWith("…", engine.Ellipsize("A very long customer name that does not fit", 50, style));
    }
}

public class TicketTests
{
    [Theory]
    [InlineData(PaperSize.A4, "en")]
    [InlineData(PaperSize.A5, "si")]
    [InlineData(PaperSize.Thermal80, "ta")]
    [InlineData(PaperSize.Thermal58, "en")]
    public void Renders_pdf_and_raster_for_every_paper(PaperSize paper, string language)
    {
        var template = new TicketTemplate { Paper = paper, Language = language };
        var pdf = TicketRenderer.RenderPdf(Samples.Ticket(), template);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 2000);

        using var bmp = TicketRenderer.RenderBitmap(Samples.Ticket(), template, 203);
        var expectedWidth = (int)Math.Ceiling(TicketRenderer.PageSize(paper).Width * 203 / 72f);
        Assert.Equal(expectedWidth, bmp.Width);
        Assert.True(bmp.Height > 300);
        // Something dark was drawn.
        Assert.Contains(Enumerable.Range(0, 400).Select(i => bmp.GetPixel(bmp.Width / 2, Math.Min(bmp.Height - 1, 40 + i))), c => c.Red < 100);

        var text = PdfText(pdf);
        if (text is not null)
        {
            Assert.Contains("CMB-2026-000042", text);
            Assert.Contains("20,200", text);
        }
    }

    [Fact]
    public void Status_banners_appear()
    {
        var manual = PdfText(TicketRenderer.RenderPdf(Samples.Ticket(manual: true), new TicketTemplate { Paper = PaperSize.A5 }));
        var cancelled = PdfText(TicketRenderer.RenderPdf(Samples.Ticket(TicketStatus.Cancelled), new TicketTemplate { Paper = PaperSize.A4 }));
        var pending = PdfText(TicketRenderer.RenderPdf(Samples.Ticket(TicketStatus.Pending), new TicketTemplate { Paper = PaperSize.Thermal80 }));
        if (manual is null) return; // pdftotext not installed
        Assert.Contains("NOT A VERIFIED INDICATOR MEASUREMENT", manual);
        Assert.Contains("CANCELLED", cancelled);
        Assert.Contains("Duplicate entry", cancelled);
        Assert.Contains("AWAITING SECOND WEIGHING", pending);
    }

    [Fact]
    public void Verification_payload_detects_forgery()
    {
        var uid = Guid.NewGuid();
        var payload = TicketVerification.CreatePayload(Samples.Key, "T-1", uid, 20_200_000);
        var parsed = TicketVerification.Parse(payload)!;
        Assert.True(TicketVerification.Verify(Samples.Key, parsed));
        Assert.False(TicketVerification.Verify(Samples.Key, parsed with { NetGrams = 30_200_000 }));
        Assert.False(TicketVerification.Verify(Encoding.UTF8.GetBytes("another-installation"), parsed));

        var url = TicketVerification.CreatePayload(Samples.Key, "CMB/42", uid, 1, "https://cloud.example.lk/");
        Assert.StartsWith("https://cloud.example.lk/verify?t=CMB%2F42&u=", url);
        var fromUrl = TicketVerification.Parse(url)!;
        Assert.Equal("CMB/42", fromUrl.TicketNo);
        Assert.True(TicketVerification.Verify(Samples.Key, fromUrl));
        Assert.Null(TicketVerification.Parse("garbage"));
    }

    [Fact]
    public void Labels_cover_all_languages()
    {
        foreach (var key in TicketLabels.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(TicketLabels.Get(key, "si")));
            Assert.False(string.IsNullOrWhiteSpace(TicketLabels.Get(key, "ta")));
        }
        Assert.Equal("NET / ශුද්ධ බර", TicketLabels.Label("net", new TicketTemplate { Language = "si" }));
        Assert.Equal("நிகர எடை", TicketLabels.Label("net", new TicketTemplate { Language = "ta", Bilingual = false }));
    }

    [Fact]
    public void Escpos_raster_and_text()
    {
        var raster = EscPos.RasterTicket(Samples.Ticket(), new TicketTemplate { Paper = PaperSize.Thermal80, Copies = 2 });
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1D, 0x76, 0x30, 0x00, 72, 0 }, raster[..8]);
        Assert.Equal(2, CountOf(raster, [0x1D, 0x56, 0x42, 0x03]));

        var text = EscPos.TextTicket(Samples.Ticket(), new TicketTemplate { Paper = PaperSize.Thermal58 });
        var ascii = Encoding.ASCII.GetString(text);
        Assert.Contains("CMB-2026-000042", ascii);
        Assert.Contains("NET 20,200 kg", ascii);
        Assert.Contains("Metal 3/4\" (?? 3/4)", ascii); // non-Latin replaced, never garbled bytes
    }

    [Fact]
    public void Raster_bit_packing()
    {
        using var bmp = new SKBitmap(10, 2);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.White);
        bmp.SetPixel(0, 0, SKColors.Black);
        bmp.SetPixel(9, 1, SKColors.Black);
        var bytes = EscPos.Raster(bmp);
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 2, 0, 2, 0, 0x80, 0x00, 0x00, 0x40 }, bytes);
    }

    private static int CountOf(byte[] data, byte[] pattern)
    {
        var n = 0;
        for (var i = 0; i + pattern.Length <= data.Length; i++) if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) n++;
        return n;
    }

    internal static string? PdfText(byte[] pdf)
    {
        if (!File.Exists("/usr/bin/pdftotext")) return null;
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, pdf);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pdftotext", $"-layout \"{path}\" -") { RedirectStandardOutput = true })!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return text;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ReportExportTests
{
    private static ReportTable Table(int rows) => new()
    {
        Title = "Material-wise report",
        Subtitle = "2026-03-01 to 2026-03-31",
        CompanyName = "Lanka Aggregates",
        GeneratedBy = "admin",
        Columns = [new("Ticket"), new("Date", ColumnKind.DateTime), new("Vehicle"), new("Material", Width: 2), new("Net", ColumnKind.Weight), new("Count", ColumnKind.Integer)],
        Rows = Enumerable.Range(0, rows).Select(i => (IReadOnlyList<object?>)new object?[]
        {
            $"T-{i:0000}", new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero).AddHours(i), $"WP CAB-{i:0000}",
            i == 0 ? "=HYPERLINK(\"http://evil\")" : i == 1 ? "Sand, \"washed\"\nfine" : "ගල් / கல்", Weight.FromKilograms(20000 + i), (long)i,
        }).ToList(),
        Totals = ["Total", null, null, null, Weight.FromKilograms(20000m * rows + rows * (rows - 1) / 2m), (long)rows],
    };

    [Fact]
    public void Csv_is_rfc4180_with_injection_guard()
    {
        var csv = Encoding.UTF8.GetString(CsvExporter.ToBytes(Table(3)));
        Assert.StartsWith("﻿Ticket,Date,Vehicle,Material,Net (kg),Count", csv);
        Assert.Contains("'=HYPERLINK(\"\"http://evil\"\")", csv);
        Assert.Contains("\"Sand, \"\"washed\"\"\nfine\"", csv);
        Assert.Contains("ගල් / கல்", csv);
        Assert.Contains("2026-03-01 08:00:00", csv.Replace("\r\n", "\n"));
        Assert.Contains(",20002,2", csv);
        Assert.EndsWith("Total,,,,60003,3\r\n", csv);
    }

    [Fact]
    public void Xlsx_has_typed_cells_and_no_formulas()
    {
        var table = Table(5);
        using var wb = new XLWorkbook(new MemoryStream(XlsxExporter.ToBytes(table)));
        var ws = wb.Worksheet(1);
        var header = ws.RowsUsed().First(r => r.Cell(1).GetString() == "Ticket");
        var first = header.RowBelow();
        Assert.Equal("T-0000", first.Cell(1).GetString());
        Assert.Equal(XLDataType.DateTime, first.Cell(2).DataType);
        Assert.Equal(XLDataType.Number, first.Cell(5).DataType);
        Assert.Equal(20000.0, first.Cell(5).GetDouble());
        Assert.False(first.Cell(4).HasFormula);
        Assert.Equal("=HYPERLINK(\"http://evil\")", first.Cell(4).GetString());
        Assert.Equal("Net (kg)", header.Cell(5).GetString());
    }

    [Fact]
    public void Pdf_report_paginates()
    {
        var pdf = PdfReportRenderer.Render(Table(150));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        var text = TicketTests.PdfText(pdf);
        if (text is null) return;
        Assert.Contains("Page 1 of", text);
        Assert.Contains("Page 3 of", text);
        Assert.Contains("T-0149", text);
        Assert.Contains("3,011,175", text); // totals row on the last page
    }

    [Fact]
    public void Empty_report_still_renders()
    {
        var t = Table(0) with { Totals = null };
        Assert.Equal("%PDF", Encoding.ASCII.GetString(PdfReportRenderer.Render(t), 0, 4));
        Assert.NotEmpty(XlsxExporter.ToBytes(t));
        Assert.Single(Encoding.UTF8.GetString(CsvExporter.ToBytes(t)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }
}
