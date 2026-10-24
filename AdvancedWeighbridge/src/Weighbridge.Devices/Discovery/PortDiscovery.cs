using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Weighbridge.Devices.Discovery;

public sealed record SerialPortInfo(
    string PortName,
    string? FriendlyName,
    string? Manufacturer,
    string? HardwareId,
    string? UsbVendorId,
    string? UsbProductId,
    string? ChipVendor,
    bool DriverProblem,
    int? ConfigManagerErrorCode,
    string? DriverHelpUrl)
{
    public string Display => FriendlyName is { Length: > 0 } f ? $"{PortName} – {f}" : PortName;
}

/// <summary>
/// Lists COM ports. On Windows it also uses WMI to show the adapter name (e.g. "USB-SERIAL CH340 (COM3)"),
/// identifies common USB-serial chips and reports adapters whose driver is missing, with the chip vendor's official
/// driver page. Drivers are never downloaded or installed silently.
/// </summary>
public static partial class PortDiscovery
{
    /// <summary>USB vendor id → (chip vendor, official driver page).</summary>
    public static IReadOnlyDictionary<string, (string Vendor, string Url)> KnownUsbSerialVendors { get; } = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["0403"] = ("FTDI", "https://ftdichip.com/drivers/vcp-drivers/"),
        ["067B"] = ("Prolific", "https://www.prolific.com.tw/"),
        ["1A86"] = ("WCH (CH340/CH341)", "https://www.wch-ic.com/downloads/CH341SER_EXE.html"),
        ["10C4"] = ("Silicon Labs (CP210x)", "https://www.silabs.com/developers/usb-to-uart-bridge-vcp-drivers"),
        ["0557"] = ("ATEN", "https://www.aten.com/global/en/supportcenter/"),
    };

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComInName();

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    public static IReadOnlyList<SerialPortInfo> List()
    {
        var names = SafeGetPortNames();
        var result = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) result[n] = new SerialPortInfo(n, null, null, null, null, null, null, false, null, null);

        if (OperatingSystem.IsWindows())
        {
            foreach (var info in QueryWmi()) result[info.PortName.Length > 0 ? info.PortName : info.HardwareId ?? Guid.NewGuid().ToString()] = info;
        }

        return result.Values
            .OrderBy(p => p.PortName.Length == 0 ? 1 : 0)
            .ThenBy(p => PortNumber(p.PortName))
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string[] SafeGetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    private static int PortNumber(string name)
    {
        var digits = new string(name.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    [SupportedOSPlatform("windows")]
    private static List<SerialPortInfo> QueryWmi()
    {
        var list = new List<SerialPortInfo>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Name LIKE '%(COM%' OR PNPDeviceID LIKE 'USB\\\\VID_%' OR PNPDeviceID LIKE 'FTDIBUS%'");
            foreach (var obj in searcher.Get())
            {
                using var mo = (System.Management.ManagementObject)obj;
                var name = mo["Name"] as string ?? string.Empty;
                var pnp = mo["PNPDeviceID"] as string ?? string.Empty;
                var manufacturer = mo["Manufacturer"] as string;
                var error = mo["ConfigManagerErrorCode"] is uint code ? (int)code : (int?)null;
                var com = ComInName().Match(name);
                var ids = VidPid().Match(pnp);
                string? vid = ids.Success ? ids.Groups[1].Value.ToUpperInvariant() : null;
                string? pid = ids.Success ? ids.Groups[2].Value.ToUpperInvariant() : null;
                if (pnp.StartsWith("FTDIBUS", StringComparison.OrdinalIgnoreCase)) vid ??= "0403";
                var known = vid is not null && KnownUsbSerialVendors.TryGetValue(vid, out var v) ? v : default;

                // Keep COM ports, and USB-serial chips whose driver is missing (they have no COM name yet).
                var driverProblem = error is not null and not 0;
                if (!com.Success && !(driverProblem && known.Vendor is not null)) continue;

                list.Add(new SerialPortInfo(
                    com.Success ? com.Groups[1].Value.ToUpperInvariant() : string.Empty,
                    name,
                    manufacturer,
                    pnp,
                    vid,
                    pid,
                    known.Vendor,
                    driverProblem,
                    error,
                    driverProblem ? known.Url : null));
            }
        }
        catch (Exception e) when (e is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            // WMI unavailable: fall back to the plain port names.
        }
        return list;
    }
}
