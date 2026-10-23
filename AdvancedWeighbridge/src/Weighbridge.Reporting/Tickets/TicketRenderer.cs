using QRCoder;
using SkiaSharp;
using Weighbridge.Core.Units;
using Weighbridge.Reporting.Text;

namespace Weighbridge.Reporting.Tickets;

internal abstract record DrawOp;
internal sealed record TextOp(float X, float Baseline, string Text, TextStyle Style, TextAlign Align) : DrawOp;
internal sealed record LineOp(float X1, float Y1, float X2, float Y2, float Width, SKColor Color, bool Dashed = false) : DrawOp;
internal sealed record RectOp(SKRect Rect, SKColor? Fill, SKColor? Stroke, float StrokeWidth) : DrawOp;
internal sealed record ImageOp(SKRect Rect, byte[] Data) : DrawOp;
internal sealed record QrOp(SKRect Rect, string Payload) : DrawOp;

/// <summary>Collects positioned drawing operations top-to-bottom; the page height follows the content.</summary>
internal sealed class Layout
{
    public Layout(float pageWidth, float margin)
    {
        PageWidth = pageWidth;
        Margin = margin;
        Y = margin;
    }

    public List<DrawOp> Ops { get; } = [];
    public float Y { get; set; }
    public float PageWidth { get; }
    public float Margin { get; }
    public float Left => Margin;
    public float Right => PageWidth - Margin;
    public float Width => PageWidth - 2 * Margin;
    public TextEngine Text { get; } = TextEngine.Shared;

    public void Line(float thickness = 0.8f, bool dashed = false, SKColor? color = null)
    {
        Ops.Add(new LineOp(Left, Y, Right, Y, thickness, color ?? SKColors.Black, dashed));
        Y += 4;
    }

    public void Space(float h) => Y += h;

    /// <summary>Adds wrapped text at the current position.</summary>
    public void Paragraph(string text, TextStyle style, TextAlign align = TextAlign.Left, float? x = null, float? width = null)
    {
        var w = width ?? Width;
        var (ascent, lh) = TextEngine.Metrics(style);
        foreach (var line in Text.Wrap(text, w, style))
        {
            var px = align switch
            {
                TextAlign.Center => (x ?? Left) + w / 2,
                TextAlign.Right => (x ?? Left) + w,
                _ => x ?? Left,
            };
            Ops.Add(new TextOp(px, Y + ascent, line, style, align));
            Y += lh;
        }
    }

    public void TextAt(float x, float y, string text, TextStyle style, TextAlign align = TextAlign.Left) =>
        Ops.Add(new TextOp(x, y + TextEngine.Metrics(style).Ascent, text, style, align));
}

/// <summary>
/// Renders weighment tickets for A4, A5 and thermal rolls. The same layout produces the PDF and the printer raster,
/// so what is printed always matches the archived PDF.
/// </summary>
public static class TicketRenderer
{
    public static (float Width, float Height) PageSize(PaperSize paper) => paper switch
    {
        PaperSize.A4 => (595.28f, 841.89f),
        PaperSize.A5 => (419.53f, 595.28f),
        PaperSize.Thermal80 => (226.77f, 0),
        _ => (164.41f, 0),
    };

    public static byte[] RenderPdf(TicketData data, TicketTemplate template)
    {
        var (width, height, ops) = BuildLayout(data, template);
        using var ms = new MemoryStream();
        using (var wstream = new SKManagedWStream(ms))
        using (var doc = SKDocument.CreatePdf(wstream, new SKDocumentPdfMetadata
        {
            Title = $"Weighment ticket {data.TicketNo}",
            Author = data.Company.Name,
            Creator = "Advanced Weighbridge Management System",
            Producer = "AWMS",
            Creation = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        }))
        {
            var canvas = doc.BeginPage(width, height);
            Draw(canvas, ops);
            doc.EndPage();
            doc.Close();
        }
        return ms.ToArray();
    }

    /// <summary>Raster of the ticket at printer resolution (e.g. 203 dpi thermal, 300 dpi laser), white background.</summary>
    public static SKBitmap RenderBitmap(TicketData data, TicketTemplate template, float dpi)
    {
        var (width, height, ops) = BuildLayout(data, template);
        var scale = dpi / 72f;
        var bmp = new SKBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        Draw(canvas, ops);
        return bmp;
    }

    public static byte[] RenderPng(TicketData data, TicketTemplate template, float dpi = 150)
    {
        using var bmp = RenderBitmap(data, template, dpi);
        using var image = SKImage.FromBitmap(bmp);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    internal static (float Width, float Height, List<DrawOp> Ops) BuildLayout(TicketData data, TicketTemplate template)
    {
        var (pageWidth, pageHeight) = PageSize(template.Paper);
        var thermal = template.Paper is PaperSize.Thermal80 or PaperSize.Thermal58;
        var layout = thermal ? BuildThermal(data, template, pageWidth) : BuildWide(data, template, pageWidth);
        var height = thermal ? layout.Y + layout.Margin : Math.Max(pageHeight, layout.Y + layout.Margin);
        return (pageWidth, height, layout.Ops);
    }

    // ------------------------------------------------------------------ A4 / A5

    private static Layout BuildWide(TicketData d, TicketTemplate t, float pageWidth)
    {
        var a4 = t.Paper == PaperSize.A4;
        var k = a4 ? 1.15f : 1f;
        var l = new Layout(pageWidth, a4 ? 36 : 22);
        var small = new TextStyle(7.5f * k);
        var normal = new TextStyle(8.6f * k);
        var bold = normal with { Bold = true };
        var label = new TextStyle(7.6f * k, Color: new SKColor(0x44, 0x44, 0x44));

        // Header: logo, company block, ticket block.
        var top = l.Y;
        var x = l.Left;
        if (t.ShowLogo && d.Company.Logo is { Length: > 0 } logo)
        {
            l.Ops.Add(new ImageOp(SKRect.Create(l.Left, top, 46 * k, 46 * k), logo));
            x += 52 * k;
        }
        var headerWidth = l.Width * 0.58f - (x - l.Left);
        l.Y = top;
        l.Paragraph(d.Company.Name, new TextStyle(13 * k, Bold: true), x: x, width: headerWidth);
        foreach (var lineText in new[] { d.Company.Address, Join(" · ", d.Company.Phone, d.Company.Email), d.Company.TaxNo is null ? null : "VAT/TIN " + d.Company.TaxNo })
        {
            if (!string.IsNullOrWhiteSpace(lineText)) l.Paragraph(lineText, small, x: x, width: headerWidth);
        }
        var leftBottom = l.Y;

        l.Y = top;
        var rightX = l.Left + l.Width * 0.58f;
        var rightW = l.Width * 0.42f;
        l.Paragraph(TicketLabels.Label("title", t), new TextStyle(11.5f * k, Bold: true), TextAlign.Right, rightX, rightW);
        l.Paragraph($"{TicketLabels.Label("ticketNo", t)}: {d.TicketNo}", new TextStyle(10 * k, Bold: true), TextAlign.Right, rightX, rightW);
        l.Paragraph($"{TicketLabels.Label("date", t)}: {FormatTime(d.Second?.At ?? d.First?.At ?? d.PrintedAt, t)}", normal, TextAlign.Right, rightX, rightW);
        if (d.ReprintNumber > 0) l.Paragraph($"{TicketLabels.Label("reprint", t)} #{d.ReprintNumber}", bold with { Color = SKColors.DarkRed }, TextAlign.Right, rightX, rightW);
        l.Y = Math.Max(leftBottom, l.Y) + 4;
        l.Line(1.2f);

        StatusBanners(l, d, t, normal);

        // Details in two columns.
        var details = new List<(string Key, string? Value)>
        {
            ("vehicle", d.VehicleReg), ("weighbridge", Join(" – ", d.WeighbridgeName, d.SiteName)),
            ("driver", d.DriverName), ("direction", d.Direction),
            ("customer", d.Customer), ("supplier", d.Supplier),
            ("material", d.Material), ("transporter", d.TransportCompany),
            ("orderNo", d.OrderNo), ("deliveryNote", d.DeliveryNote),
            ("reference", d.Reference), ("mode", d.ModeLabel),
        };
        // Each cell: small grey label above a bold value, so long bilingual labels never collide with values.
        var filled = details.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToList();
        var colW = l.Width / 2;
        var labelLh = TextEngine.Metrics(label).LineHeight;
        for (var i = 0; i < filled.Count; i += 2)
        {
            var rowHeight = 0f;
            for (var c = 0; c < 2 && i + c < filled.Count; c++)
            {
                var (key, value) = filled[i + c];
                var cx = l.Left + c * colW;
                l.TextAt(cx, l.Y, l.Text.Ellipsize(TicketLabels.Inline(key, t), colW - 8, label), label);
                var vStyle = key == "vehicle" ? bold with { Size = bold.Size * 1.3f } : bold;
                l.TextAt(cx, l.Y + labelLh - 1, l.Text.Ellipsize(value!, colW - 8, vStyle), vStyle);
                rowHeight = Math.Max(rowHeight, labelLh + TextEngine.Metrics(vStyle).LineHeight + 2);
            }
            l.Y += rowHeight;
        }
        l.Space(1);
        l.Line();

        // Weighings table: label | weight | date & time | source.
        var cols = new[] { 0f, 0.30f, 0.52f, 0.76f };
        var unit = WeightUnits.Symbol(t.Display.Unit);
        string[] headers = [string.Empty, $"{TicketLabels.Inline("weight", t)} ({unit})", TicketLabels.Inline("time", t), TicketLabels.Inline("source", t)];
        for (var i = 1; i < headers.Length; i++)
        {
            var w = (i + 1 < cols.Length ? cols[i + 1] : 1f) * l.Width - cols[i] * l.Width - 6;
            var x0 = l.Left + cols[i] * l.Width;
            if (i == 1) l.TextAt(x0 + w, l.Y, l.Text.Ellipsize(headers[i], w, label), label, TextAlign.Right);
            else l.TextAt(x0, l.Y, l.Text.Ellipsize(headers[i], w, label), label);
        }
        l.Y += labelLh + 1;
        foreach (var (key, w) in new[] { ("first", d.First), ("second", d.Second) })
        {
            if (w is null) continue;
            var (primary, secondary) = TicketLabels.Pair(key, t);
            var weightW = (cols[2] - cols[1]) * l.Width - 6;
            l.TextAt(l.Left, l.Y, l.Text.Ellipsize(primary, cols[1] * l.Width - 6, bold), bold);
            l.TextAt(l.Left + cols[1] * l.Width + weightW, l.Y, t.Display.FormatNumber(w.Weight), bold, TextAlign.Right);
            l.TextAt(l.Left + cols[2] * l.Width, l.Y, FormatTime(w.At, t), normal);
            l.TextAt(l.Left + cols[3] * l.Width, l.Y, l.Text.Ellipsize(w.SourceLabel, (1 - cols[3]) * l.Width, normal), w.IsManual ? bold with { Color = SKColors.DarkRed } : normal);
            l.Y += TextEngine.Metrics(bold).LineHeight;
            if (secondary is not null)
            {
                l.TextAt(l.Left, l.Y - 2, l.Text.Ellipsize(secondary, cols[1] * l.Width - 6, label), label);
                l.Y += labelLh - 2;
            }
            l.Y += 2;
        }
        l.Space(4);

        // Gross / tare / net summary with a prominent net box.
        if (d.Net is { } net)
        {
            var boxTop = l.Y;
            var boxH = 46 * k;
            var third = l.Width / 3;
            DrawFigure(l, l.Left, boxTop, third - 6, boxH, TicketLabels.Label("gross", t), d.Gross, t, k, emphasis: false);
            DrawFigure(l, l.Left + third, boxTop, third - 6, boxH, TicketLabels.Label("tare", t) + (d.TareMethodLabel is null ? string.Empty : $" ({d.TareMethodLabel})"), d.Tare, t, k, emphasis: false);
            DrawFigure(l, l.Left + 2 * third, boxTop, third, boxH, TicketLabels.Label("net", t), net, t, k, emphasis: true);
            l.Y = boxTop + boxH + 8;
        }
        else if (d.First is { } first)
        {
            DrawFigure(l, l.Left, l.Y, l.Width, 40 * k, TicketLabels.Label("first", t), first.Weight, t, k, emphasis: true);
            l.Y += 48 * k;
        }

        if (!string.IsNullOrWhiteSpace(d.Remarks))
        {
            l.Paragraph($"{TicketLabels.Label("remarks", t)}: {d.Remarks}", normal);
            l.Space(2);
        }

        // Camera evidence thumbnails.
        if (t.ShowImages && d.Images.Count > 0)
        {
            var count = Math.Min(d.Images.Count, 3);
            var gap = 6f;
            var w = (l.Width - gap * (count - 1)) / count;
            var h = w * 0.5625f;
            for (var i = 0; i < count; i++) l.Ops.Add(new ImageOp(SKRect.Create(l.Left + i * (w + gap), l.Y, w, h), d.Images[i]));
            l.Y += h + 6;
        }

        // QR + signatures.
        var footerTop = l.Y + 4;
        var qrSize = 64 * k;
        var sigLeft = l.Left;
        var footerBottom = footerTop;
        if (t.ShowQrCode && !string.IsNullOrEmpty(d.VerificationPayload))
        {
            l.Ops.Add(new QrOp(SKRect.Create(l.Left, footerTop, qrSize, qrSize), d.VerificationPayload));
            var save = l.Y;
            l.Y = footerTop + qrSize + 1;
            l.Paragraph(TicketLabels.Get("verify", t.Language), small, TextAlign.Center, l.Left - 4, qrSize + 8);
            footerBottom = l.Y;
            l.Y = save;
            sigLeft += qrSize + 14;
        }
        if (t.ShowSignatures)
        {
            var sigs = new[] { ("operatorSignature", d.OperatorName), ("driverSignature", d.DriverName), ("authorizedSignature", (string?)null) };
            var sw = (l.Right - sigLeft) / sigs.Length;
            var lineY = footerTop + qrSize * 0.55f;
            var smallLh = TextEngine.Metrics(small).LineHeight;
            for (var i = 0; i < sigs.Length; i++)
            {
                var sx = sigLeft + i * sw;
                var cellW = sw - 12;
                l.Ops.Add(new LineOp(sx + 4, lineY, sx + 4 + cellW, lineY, 0.6f, SKColors.Black, Dashed: true));
                var (primary, secondary) = TicketLabels.Pair(sigs[i].Item1, t);
                l.TextAt(sx + 4, lineY + 2, l.Text.Ellipsize(primary, cellW, small), small);
                if (secondary is not null) l.TextAt(sx + 4, lineY + 2 + smallLh, l.Text.Ellipsize(secondary, cellW, small), small);
                if (!string.IsNullOrWhiteSpace(sigs[i].Item2)) l.TextAt(sx + 4, lineY - smallLh - 2, l.Text.Ellipsize(sigs[i].Item2!, cellW, small), small);
                footerBottom = Math.Max(footerBottom, lineY + 2 + smallLh * (secondary is null ? 1 : 2));
            }
        }
        l.Y = Math.Max(footerBottom, footerTop + qrSize) + 8;
        l.Line(0.5f);
        l.Paragraph(Join("  ·  ", $"{TicketLabels.Inline("printed", t)}: {FormatTime(d.PrintedAt, t)}", d.OperatorName is null ? null : $"{TicketLabels.Inline("operator", t)}: {d.OperatorName}") ?? string.Empty, small);
        l.Paragraph($"ID {d.TransactionUid:D}", small with { Color = SKColors.DimGray });
        if (!string.IsNullOrWhiteSpace(t.Footer)) l.Paragraph(t.Footer, small, TextAlign.Center);
        return l;
    }

    private static void DrawFigure(Layout l, float x, float y, float w, float h, string caption, Weight? weight, TicketTemplate t, float k, bool emphasis)
    {
        l.Ops.Add(new RectOp(SKRect.Create(x, y, w, h), emphasis ? new SKColor(0xEE, 0xF3, 0xFB) : null, SKColors.Black, emphasis ? 1.6f : 0.7f));
        l.TextAt(x + 6, y + 4, caption, new TextStyle(7.5f * k, Bold: true));
        var text = weight is { } wt ? t.Display.Format(wt) : "—";
        var size = (emphasis ? 19f : 15f) * k;
        var style = new TextStyle(size, Bold: true, Mono: false);
        while (l.Text.Measure(text, style) > w - 12 && style.Size > 8) style = style with { Size = style.Size - 1 };
        l.TextAt(x + w - 6, y + h - style.Size * 1.3f, text, style, TextAlign.Right);
    }

    private static void StatusBanners(Layout l, TicketData d, TicketTemplate t, TextStyle normal)
    {
        var bannerStyle = normal with { Bold = true, Color = SKColors.White };
        void Banner(string text, SKColor fill)
        {
            var lines = l.Text.Wrap(text, l.Width - 12, bannerStyle);
            var h = lines.Count * TextEngine.Metrics(bannerStyle).LineHeight + 6;
            l.Ops.Add(new RectOp(SKRect.Create(l.Left, l.Y, l.Width, h), fill, null, 0));
            l.Y += 3;
            l.Paragraph(text, bannerStyle, TextAlign.Center, l.Left + 6, l.Width - 12);
            l.Y += 6;
        }
        if (d.Status == TicketStatus.Cancelled) Banner(TicketLabels.Label("cancelled", t) + (d.CancelReason is null ? string.Empty : $": {d.CancelReason}"), new SKColor(0xB0, 0x00, 0x20));
        if (d.Status == TicketStatus.Pending) Banner(TicketLabels.Label("pending", t), new SKColor(0x8A, 0x5A, 0x00));
        if (d.HasManualWeight) Banner(TicketLabels.Label("manual", t), new SKColor(0xB0, 0x30, 0x00));
        if (d.HasOverride) Banner(TicketLabels.Label("override", t), new SKColor(0x6A, 0x3D, 0x9A));
    }

    // ------------------------------------------------------------------ thermal rolls

    private static Layout BuildThermal(TicketData d, TicketTemplate t, float pageWidth)
    {
        var narrow = t.Paper == PaperSize.Thermal58;
        var l = new Layout(pageWidth, narrow ? 8 : 11);
        var k = narrow ? 0.85f : 1f;
        var small = new TextStyle(7f * k);
        var normal = new TextStyle(8.2f * k);
        var bold = normal with { Bold = true };

        if (t.ShowLogo && d.Company.Logo is { Length: > 0 } logo)
        {
            var size = 40 * k;
            l.Ops.Add(new ImageOp(SKRect.Create(l.Left + (l.Width - size) / 2, l.Y, size, size), logo));
            l.Y += size + 4;
        }
        l.Paragraph(d.Company.Name, new TextStyle(11 * k, Bold: true), TextAlign.Center);
        foreach (var s in new[] { d.Company.Address, Join(" · ", d.Company.Phone, d.Company.TaxNo is null ? null : "VAT " + d.Company.TaxNo) })
        {
            if (!string.IsNullOrWhiteSpace(s)) l.Paragraph(s, small, TextAlign.Center);
        }
        l.Space(3);
        l.Line(0.6f, dashed: true);
        l.Paragraph(TicketLabels.Label("title", t), new TextStyle(9.5f * k, Bold: true), TextAlign.Center);
        l.Paragraph(d.TicketNo, new TextStyle(10.5f * k, Bold: true), TextAlign.Center);
        if (d.ReprintNumber > 0) l.Paragraph($"{TicketLabels.Label("reprint", t)} #{d.ReprintNumber}", bold, TextAlign.Center);
        if (d.Status == TicketStatus.Cancelled) l.Paragraph("*** " + TicketLabels.Label("cancelled", t) + " ***", bold, TextAlign.Center);
        if (d.Status == TicketStatus.Pending) l.Paragraph(TicketLabels.Label("pending", t), bold, TextAlign.Center);
        if (d.HasManualWeight) l.Paragraph("! " + TicketLabels.Label("manual", t), bold, TextAlign.Center);
        if (d.HasOverride) l.Paragraph(TicketLabels.Label("override", t), small, TextAlign.Center);
        l.Line(0.6f, dashed: true);

        void Row(string key, string? value, TextStyle? style = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var lab = TicketLabels.Get(key, t.Language);
            var valueStyle = style ?? bold;
            var labelWidth = l.Width * 0.42f;
            l.TextAt(l.Left, l.Y, l.Text.Ellipsize(lab, labelWidth - 2, small), small);
            var lines = l.Text.Wrap(value, l.Width - labelWidth, valueStyle);
            foreach (var line in lines)
            {
                l.TextAt(l.Right, l.Y, line, valueStyle, TextAlign.Right);
                l.Y += TextEngine.Metrics(valueStyle).LineHeight;
            }
        }

        Row("date", FormatTime(d.Second?.At ?? d.First?.At ?? d.PrintedAt, t), normal);
        Row("vehicle", d.VehicleReg, bold with { Size = bold.Size * 1.2f });
        Row("driver", d.DriverName);
        Row("customer", d.Customer);
        Row("supplier", d.Supplier);
        Row("material", d.Material);
        Row("orderNo", d.OrderNo);
        Row("reference", d.Reference);
        Row("weighbridge", d.WeighbridgeName, normal);
        l.Line(0.6f, dashed: true);
        if (d.First is { } f)
        {
            Row("first", t.Display.Format(f.Weight));
            l.TextAt(l.Right, l.Y, FormatTime(f.At, t) + " · " + f.SourceLabel, small, TextAlign.Right);
            l.Y += TextEngine.Metrics(small).LineHeight;
        }
        if (d.Second is { } s2)
        {
            Row("second", t.Display.Format(s2.Weight));
            l.TextAt(l.Right, l.Y, FormatTime(s2.At, t) + " · " + s2.SourceLabel, small, TextAlign.Right);
            l.Y += TextEngine.Metrics(small).LineHeight;
        }
        if (d.Net is { } net)
        {
            l.Line(0.6f, dashed: true);
            Row("gross", d.Gross is { } g ? t.Display.Format(g) : null);
            Row("tare", d.Tare is { } ta ? t.Display.Format(ta) + (d.TareMethodLabel is null ? string.Empty : $" ({d.TareMethodLabel})") : null);
            l.Space(2);
            var netStyle = new TextStyle(17 * k, Bold: true);
            var text = t.Display.Format(net);
            while (l.Text.Measure(text, netStyle) > l.Width * 0.7f && netStyle.Size > 9) netStyle = netStyle with { Size = netStyle.Size - 1 };
            var h = TextEngine.Metrics(netStyle).LineHeight + 8;
            l.Ops.Add(new RectOp(SKRect.Create(l.Left, l.Y, l.Width, h), null, SKColors.Black, 1.4f));
            l.TextAt(l.Left + 4, l.Y + 4, TicketLabels.Get("net", t.Language), bold);
            l.TextAt(l.Right - 4, l.Y + 4, text, netStyle, TextAlign.Right);
            l.Y += h + 4;
        }
        if (!string.IsNullOrWhiteSpace(d.Remarks)) l.Paragraph(d.Remarks, small);
        if (t.ShowQrCode && !string.IsNullOrEmpty(d.VerificationPayload))
        {
            var size = Math.Min(l.Width * 0.55f, 96);
            l.Space(4);
            l.Ops.Add(new QrOp(SKRect.Create(l.Left + (l.Width - size) / 2, l.Y, size, size), d.VerificationPayload));
            l.Y += size + 2;
            l.Paragraph(TicketLabels.Get("verify", t.Language), small, TextAlign.Center);
        }
        if (t.ShowSignatures)
        {
            l.Space(18);
            l.Ops.Add(new LineOp(l.Left + l.Width * 0.15f, l.Y, l.Right - l.Width * 0.15f, l.Y, 0.6f, SKColors.Black, Dashed: true));
            l.Space(2);
            l.Paragraph(TicketLabels.Get("driverSignature", t.Language), small, TextAlign.Center);
        }
        l.Line(0.6f, dashed: true);
        l.Paragraph($"{TicketLabels.Get("printed", t.Language)} {FormatTime(d.PrintedAt, t)}" + (d.OperatorName is null ? string.Empty : $" · {d.OperatorName}"), small, TextAlign.Center);
        if (!string.IsNullOrWhiteSpace(t.Footer)) l.Paragraph(t.Footer, small, TextAlign.Center);
        l.Space(6);
        return l;
    }

    // ------------------------------------------------------------------ drawing

    internal static void Draw(SKCanvas canvas, IEnumerable<DrawOp> ops)
    {
        var text = TextEngine.Shared;
        foreach (var op in ops)
        {
            switch (op)
            {
                case TextOp t:
                    text.Draw(canvas, t.Text, t.X, t.Baseline, t.Style, t.Align);
                    break;
                case LineOp line:
                {
                    using var paint = new SKPaint { Color = line.Color, StrokeWidth = line.Width, IsAntialias = true, Style = SKPaintStyle.Stroke };
                    if (line.Dashed) paint.PathEffect = SKPathEffect.CreateDash([3, 2], 0);
                    canvas.DrawLine(line.X1, line.Y1, line.X2, line.Y2, paint);
                    break;
                }
                case RectOp r:
                {
                    if (r.Fill is { } fill)
                    {
                        using var p = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
                        canvas.DrawRect(r.Rect, p);
                    }
                    if (r.Stroke is { } stroke)
                    {
                        using var p = new SKPaint { Color = stroke, Style = SKPaintStyle.Stroke, StrokeWidth = r.StrokeWidth, IsAntialias = true };
                        canvas.DrawRect(r.Rect, p);
                    }
                    break;
                }
                case ImageOp img:
                {
                    using var image = SKImage.FromEncodedData(img.Data);
                    if (image is null) break;
                    var fit = Fit(image.Width, image.Height, img.Rect);
                    canvas.DrawImage(image, fit, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    using var border = new SKPaint { Color = new SKColor(0x99, 0x99, 0x99), Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f };
                    canvas.DrawRect(fit, border);
                    break;
                }
                case QrOp qr:
                    DrawQr(canvas, qr.Payload, qr.Rect);
                    break;
            }
        }
    }

    internal static void DrawQr(SKCanvas canvas, string payload, SKRect rect)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var n = matrix.Count;
        var module = Math.Min(rect.Width, rect.Height) / n;
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (matrix[y][x]) canvas.DrawRect(rect.Left + x * module, rect.Top + y * module, module + 0.02f, module + 0.02f, paint);
            }
        }
    }

    private static SKRect Fit(int w, int h, SKRect box)
    {
        var scale = Math.Min(box.Width / w, box.Height / h);
        var fw = w * scale;
        var fh = h * scale;
        return SKRect.Create(box.Left + (box.Width - fw) / 2, box.Top + (box.Height - fh) / 2, fw, fh);
    }

    internal static string FormatTime(DateTimeOffset at, TicketTemplate t)
    {
        var zone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(t.TimeZoneId))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(t.TimeZoneId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.ConvertTime(at, zone).ToString(t.DateFormat, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}
