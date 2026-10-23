using System.Text;
using SkiaSharp;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Reporting.Printing;

/// <summary>
/// ESC/POS output for receipt printers driven in RAW mode (no Windows driver needed). Tickets are sent as a raster
/// image (GS v 0) rendered from the same layout as the PDF, so Sinhala and Tamil print correctly on any
/// ESC/POS printer; a plain-text mode is available for very old printers (ASCII only).
/// </summary>
public static class EscPos
{
    private static readonly byte[] Init = [0x1B, 0x40];
    private static readonly byte[] CutPartial = [0x1D, 0x56, 0x42, 0x03];

    /// <summary>Dots per line: 576 for 80 mm, 384 for 58 mm printers at 203 dpi.</summary>
    public static int DotsFor(PaperSize paper) => paper == PaperSize.Thermal58 ? 384 : 576;

    public static byte[] RasterTicket(TicketData data, TicketTemplate template, bool cut = true)
    {
        var dots = DotsFor(template.Paper);
        var paper = template.Paper is PaperSize.Thermal80 or PaperSize.Thermal58 ? template.Paper : PaperSize.Thermal80;
        var widthPt = TicketRenderer.PageSize(paper).Width;
        var dpi = dots / (widthPt / 72f);
        using var bitmap = TicketRenderer.RenderBitmap(data, template with { Paper = paper }, dpi);
        var output = new List<byte>(bitmap.Width * bitmap.Height / 8 + 64);
        for (var copy = 0; copy < Math.Max(1, template.Copies); copy++)
        {
            output.AddRange(Init);
            output.AddRange(Raster(bitmap));
            output.AddRange([0x1B, 0x64, 0x04]); // feed 4 lines
            if (cut) output.AddRange(CutPartial);
        }
        return output.ToArray();
    }

    /// <summary>GS v 0 raster bit image, 1 bit per dot, black where luminance is below the threshold.</summary>
    public static byte[] Raster(SKBitmap bitmap, byte threshold = 160)
    {
        var widthBytes = (bitmap.Width + 7) / 8;
        var height = bitmap.Height;
        var result = new List<byte>(8 + widthBytes * height);
        // Send in bands of at most 255 rows so that printers with small buffers cope.
        for (var bandTop = 0; bandTop < height; bandTop += 255)
        {
            var rows = Math.Min(255, height - bandTop);
            result.AddRange([0x1D, 0x76, 0x30, 0x00, (byte)(widthBytes & 0xFF), (byte)(widthBytes >> 8), (byte)(rows & 0xFF), (byte)(rows >> 8)]);
            for (var y = bandTop; y < bandTop + rows; y++)
            {
                for (var bx = 0; bx < widthBytes; bx++)
                {
                    byte b = 0;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var x = bx * 8 + bit;
                        if (x >= bitmap.Width) continue;
                        var c = bitmap.GetPixel(x, y);
                        var lum = (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000;
                        if (c.Alpha > 0 && lum < threshold) b |= (byte)(0x80 >> bit);
                    }
                    result.Add(b);
                }
            }
        }
        return result.ToArray();
    }

    /// <summary>Text-mode ticket (ASCII only) with a native QR code (GS ( k), for printers without raster support.</summary>
    public static byte[] TextTicket(TicketData d, TicketTemplate template, bool cut = true)
    {
        var width = template.Paper == PaperSize.Thermal58 ? 32 : 48;
        var o = new List<byte>();
        void Bytes(params byte[] b) => o.AddRange(b);
        void Line(string s = "") => o.AddRange(Encoding.ASCII.GetBytes(Ascii(s) + "\n"));
        void Pair(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var v = Ascii(value);
            var space = Math.Max(1, width - label.Length - v.Length);
            Line(label + new string(' ', space) + v);
        }

        Bytes(Init);
        Bytes(0x1B, 0x61, 0x01); // centre
        Bytes(0x1B, 0x45, 0x01);
        Line(d.Company.Name);
        Bytes(0x1B, 0x45, 0x00);
        if (!string.IsNullOrWhiteSpace(d.Company.Address)) Line(d.Company.Address);
        Line("WEIGHMENT TICKET");
        Bytes(0x1D, 0x21, 0x01); // double height
        Line(d.TicketNo);
        Bytes(0x1D, 0x21, 0x00);
        if (d.ReprintNumber > 0) Line($"REPRINT #{d.ReprintNumber}");
        if (d.Status == TicketStatus.Cancelled) Line("*** CANCELLED ***");
        if (d.HasManualWeight) Line("! MANUAL ENTRY - NOT VERIFIED");
        Bytes(0x1B, 0x61, 0x00);
        Line(new string('-', width));
        Pair("Date", TicketRenderer.FormatTime(d.Second?.At ?? d.First?.At ?? d.PrintedAt, template));
        Pair("Vehicle", d.VehicleReg);
        Pair("Customer", d.Customer);
        Pair("Supplier", d.Supplier);
        Pair("Material", d.Material);
        if (d.First is { } f) Pair("First", template.Display.Format(f.Weight));
        if (d.Second is { } s) Pair("Second", template.Display.Format(s.Weight));
        if (d.Gross is { } g) Pair("Gross", template.Display.Format(g));
        if (d.Tare is { } t) Pair("Tare", template.Display.Format(t));
        if (d.Net is { } n)
        {
            Bytes(0x1B, 0x45, 0x01, 0x1D, 0x21, 0x11);
            Line("NET " + template.Display.Format(n));
            Bytes(0x1D, 0x21, 0x00, 0x1B, 0x45, 0x00);
        }
        Line(new string('-', width));
        if (template.ShowQrCode && !string.IsNullOrEmpty(d.VerificationPayload))
        {
            Bytes(0x1B, 0x61, 0x01);
            var data = Encoding.ASCII.GetBytes(d.VerificationPayload);
            var len = data.Length + 3;
            Bytes(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00); // model 2
            Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x05);       // module size
            Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31);       // ECC M
            Bytes(0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30);
            o.AddRange(data);
            Bytes(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);       // print
            Bytes(0x1B, 0x61, 0x00);
        }
        Line($"Printed {TicketRenderer.FormatTime(d.PrintedAt, template)}");
        Bytes(0x1B, 0x64, 0x04);
        if (cut) Bytes(CutPartial);
        return o.ToArray();
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (c is >= ' ' and < (char)0x7F) sb.Append(c);
            else if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append('?');
        }
        return sb.ToString();
    }
}
