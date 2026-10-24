using System.ComponentModel;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Weighbridge.Application.Weighing;
using Weighbridge.Reporting.Printing;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Desktop.Infrastructure;

/// <summary>
/// Prints tickets through the Windows spooler. Normal printers receive the same raster as the PDF layout (so the
/// printed ticket and the PDF are identical, including Sinhala and Tamil text); receipt printers can instead receive
/// ESC/POS raster bytes directly.
/// </summary>
public sealed class WpfPrintService : IPrintService
{
    public Task PrintAsync(TicketData data, TicketTemplate template, int copies, string? printerName, bool escPosRaw, CancellationToken ct) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Print(data, template, copies, printerName, escPosRaw)).Task;

    public static IReadOnlyList<string> Printers()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).Select(q => q.FullName).OrderBy(n => n).ToList();
        }
        catch (Exception e) when (e is PrintSystemException or Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    private static void Print(TicketData data, TicketTemplate template, int copies, string? printerName, bool escPosRaw)
    {
        copies = Math.Clamp(copies, 1, 10);
        if (escPosRaw)
        {
            if (string.IsNullOrWhiteSpace(printerName)) throw new InvalidOperationException("Select the receipt printer in Settings → Printing for ESC/POS printing.");
            var bytes = EscPos.RasterTicket(data, template);
            for (var i = 0; i < copies; i++) RawPrinter.Send(printerName, bytes, $"Ticket {data.TicketNo}");
            return;
        }

        var queue = OpenQueue(printerName);
        var dialog = new PrintDialog { PrintQueue = queue };
        var dpi = template.Paper is PaperSize.Thermal58 or PaperSize.Thermal80 ? 203f : 300f;
        using var bitmap = TicketRenderer.RenderBitmap(data, template, dpi);
        var image = ToBitmapSource(bitmap);
        var (widthPt, _) = TicketRenderer.PageSize(template.Paper);
        var widthDip = widthPt * 96.0 / 72.0;
        var heightDip = widthDip * image.PixelHeight / image.PixelWidth;
        for (var i = 0; i < copies; i++)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, widthDip, heightDip));
            dialog.PrintVisual(visual, $"Ticket {data.TicketNo}");
        }
    }

    private static PrintQueue OpenQueue(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return LocalPrintServer.GetDefaultPrintQueue();
        var server = new LocalPrintServer();
        var queue = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .FirstOrDefault(q => string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase));
        return queue ?? throw new InvalidOperationException($"The printer '{printerName}' is not installed on this PC. Check Settings → Printing.");
    }

    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(data.ToArray());
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// <summary>Sends bytes unchanged to a Windows printer queue (RAW data type), as needed for ESC/POS receipt printers.</summary>
public static class RawPrinter
{
    public static void Send(string printerName, byte[] bytes, string documentName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open printer '{printerName}'.");
        try
        {
            var info = new DocInfo { DocName = documentName, DataType = "RAW" };
            if (!StartDocPrinter(handle, 1, info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed.");
            try
            {
                if (!StartPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed.");
                var unmanaged = Marshal.AllocCoTaskMem(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                    if (!WritePrinter(handle, unmanaged, bytes.Length, out var written) || written != bytes.Length)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Not all data could be sent to the printer.");
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(unmanaged);
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName = string.Empty;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

#pragma warning disable SYSLIB1054 // classic P/Invoke with a by-reference class marshaller
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr handle, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);
#pragma warning restore SYSLIB1054
}
