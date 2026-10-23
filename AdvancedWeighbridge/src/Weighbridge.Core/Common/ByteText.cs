using System.Globalization;
using System.Text;

namespace Weighbridge.Core.Common;

/// <summary>
/// Converts between raw bytes and the human-editable notation used in device profiles and the raw-data monitor.
/// Supported notation: <c>&lt;STX&gt;</c>-style ASCII control names, C escapes (<c>\r \n \t \\ \xHH</c>) and literal text.
/// </summary>
public static class ByteText
{
    private static readonly string[] ControlNames =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    private static readonly Dictionary<string, byte> ControlByName = BuildControlMap();

    private static Dictionary<string, byte> BuildControlMap()
    {
        var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ControlNames.Length; i++) map[ControlNames[i]] = (byte)i;
        map["DEL"] = 0x7F;
        map["TAB"] = 0x09;
        map["SP"] = 0x20;
        map["XON"] = 0x11;
        map["XOFF"] = 0x13;
        return map;
    }

    /// <summary>Parses profile notation such as <c>"&lt;STX&gt;"</c>, <c>"\r\n"</c>, <c>"R\r"</c> or <c>"\x05"</c> into bytes.</summary>
    public static byte[] Unescape(string? notation)
    {
        if (string.IsNullOrEmpty(notation)) return [];
        var bytes = new List<byte>(notation.Length);
        for (var i = 0; i < notation.Length; i++)
        {
            var c = notation[i];
            if (c == '<')
            {
                var close = notation.IndexOf('>', i + 1);
                if (close > i + 1)
                {
                    var name = notation.Substring(i + 1, close - i - 1);
                    if (ControlByName.TryGetValue(name, out var b))
                    {
                        bytes.Add(b);
                        i = close;
                        continue;
                    }
                    if (name.Length == 4 && name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        && byte.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hb))
                    {
                        bytes.Add(hb);
                        i = close;
                        continue;
                    }
                }
            }
            else if (c == '\\' && i + 1 < notation.Length)
            {
                var n = notation[i + 1];
                switch (n)
                {
                    case 'r': bytes.Add(0x0D); i++; continue;
                    case 'n': bytes.Add(0x0A); i++; continue;
                    case 't': bytes.Add(0x09); i++; continue;
                    case '0': bytes.Add(0x00); i++; continue;
                    case '\\': bytes.Add((byte)'\\'); i++; continue;
                    case 'x':
                        // \xHH needs exactly two hex digits after the 'x'.
                        if (i + 3 < notation.Length
                            && byte.TryParse(notation.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var xb))
                        {
                            bytes.Add(xb);
                            i += 3;
                            continue;
                        }
                        break;
                }
            }
            if (c > 0xFF) throw new FormatException($"Character '{c}' cannot be sent as a single byte.");
            bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }

    /// <summary>Renders bytes as readable ASCII, with control characters shown as <c>&lt;CR&gt;</c> etc.</summary>
    public static string ToDisplay(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length + 8);
        foreach (var b in data)
        {
            if (b < 0x20) sb.Append('<').Append(ControlNames[b]).Append('>');
            else if (b == 0x7F) sb.Append("<DEL>");
            else if (b < 0x7F) sb.Append((char)b);
            else sb.Append("<0x").Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append('>');
        }
        return sb.ToString();
    }

    /// <summary>Profile notation for bytes (round-trips through <see cref="Unescape"/>).</summary>
    public static string ToNotation(ReadOnlySpan<byte> data) => ToDisplay(data);

    public static string ToHex(ReadOnlySpan<byte> data, string separator = " ")
    {
        if (data.IsEmpty) return string.Empty;
        var sb = new StringBuilder(data.Length * (2 + separator.Length));
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static byte[] FromHex(string hex)
    {
        var clean = new StringBuilder(hex.Length);
        foreach (var ch in hex)
        {
            if (Uri.IsHexDigit(ch)) clean.Append(ch);
            else if (!char.IsWhiteSpace(ch) && ch != '-' && ch != ':' && ch != ',') throw new FormatException($"Invalid hex character '{ch}'.");
        }
        if (clean.Length % 2 != 0) throw new FormatException("Hex text must contain an even number of digits.");
        return Convert.FromHexString(clean.ToString());
    }

    /// <summary>Classic 16-bytes-per-line hex dump for the raw-data monitor.</summary>
    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var line = data.Slice(offset, Math.Min(16, data.Length - offset));
            sb.Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
