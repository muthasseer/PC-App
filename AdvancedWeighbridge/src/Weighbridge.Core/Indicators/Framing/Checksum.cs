using System.Globalization;

namespace Weighbridge.Core.Indicators.Framing;

public enum ChecksumKind
{
    None = 0,

    /// <summary>XOR of all covered bytes.</summary>
    Xor8 = 1,

    /// <summary>Sum of all covered bytes modulo 256.</summary>
    Sum8 = 2,

    /// <summary>Two's complement of the 8-bit sum (sum + checksum == 0 mod 256). Also called LRC.</summary>
    TwosComplement8 = 3,

    /// <summary>Two's complement of the low 7 bits of the sum (Mettler Toledo continuous output).</summary>
    TwosComplement7 = 4,

    /// <summary>CRC-16/MODBUS (poly 0xA001 reflected, init 0xFFFF), transmitted low byte first.</summary>
    Crc16Modbus = 5,
}

public enum ChecksumLocation
{
    /// <summary>The checksum is the frame trailer (bytes after the end marker).</summary>
    Trailer = 0,

    /// <summary>The checksum is the last N bytes of the payload.</summary>
    EndOfPayload = 1,
}

public sealed record ChecksumOptions
{
    public ChecksumKind Kind { get; init; } = ChecksumKind.None;
    public ChecksumLocation Location { get; init; } = ChecksumLocation.Trailer;

    /// <summary>The checksum is sent as two ASCII hex characters instead of one raw byte.</summary>
    public bool AsciiHex { get; init; }

    /// <summary>Include the start marker in the covered bytes.</summary>
    public bool IncludeStartMarker { get; init; } = true;

    /// <summary>Include the end marker in the covered bytes.</summary>
    public bool IncludeEndMarker { get; init; } = true;
}

public readonly record struct ChecksumResult(bool Valid, string? Expected, string? Actual)
{
    public static ChecksumResult NotChecked => new(true, null, null);
}

public static class ChecksumValidator
{
    public static int ByteLength(ChecksumKind kind) => kind switch
    {
        ChecksumKind.None => 0,
        ChecksumKind.Crc16Modbus => 2,
        _ => 1,
    };

    public static ChecksumResult Validate(RawFrame frame, FramingOptions framing, ChecksumOptions options)
    {
        if (options.Kind == ChecksumKind.None) return ChecksumResult.NotChecked;

        var raw = ByteLength(options.Kind);
        var transmittedLength = options.AsciiHex ? raw * 2 : raw;
        var start = Common.ByteText.Unescape(framing.StartMarker);
        var end = Common.ByteText.Unescape(framing.EndMarker);

        byte[] covered;
        byte[] transmitted;
        if (options.Location == ChecksumLocation.Trailer)
        {
            if (frame.Trailer.Length < transmittedLength) return new ChecksumResult(false, null, "missing");
            transmitted = frame.Trailer[..transmittedLength];
            var bodyEnd = frame.Full.Length - frame.Trailer.Length;
            var from = options.IncludeStartMarker || start.Length == 0 ? 0 : start.Length;
            var to = options.IncludeEndMarker || end.Length == 0 ? bodyEnd : bodyEnd - end.Length;
            covered = frame.Full[from..Math.Max(from, to)];
        }
        else
        {
            if (frame.Payload.Length < transmittedLength) return new ChecksumResult(false, null, "missing");
            transmitted = frame.Payload[^transmittedLength..];
            var payloadBody = frame.Payload[..^transmittedLength];
            var prefix = options.IncludeStartMarker ? start : [];
            covered = [.. prefix, .. payloadBody];
        }

        var expected = Compute(options.Kind, covered);
        byte[] actual;
        if (options.AsciiHex)
        {
            var text = System.Text.Encoding.ASCII.GetString(transmitted);
            if (!TryParseHex(text, out actual)) return new ChecksumResult(false, Format(expected), text);
        }
        else
        {
            actual = transmitted;
        }
        var valid = expected.AsSpan().SequenceEqual(actual);
        return new ChecksumResult(valid, Format(expected), Format(actual));
    }

    public static byte[] Compute(ChecksumKind kind, ReadOnlySpan<byte> data)
    {
        switch (kind)
        {
            case ChecksumKind.None:
                return [];
            case ChecksumKind.Xor8:
            {
                byte x = 0;
                foreach (var b in data) x ^= b;
                return [x];
            }
            case ChecksumKind.Sum8:
            {
                var s = 0;
                foreach (var b in data) s += b;
                return [(byte)(s & 0xFF)];
            }
            case ChecksumKind.TwosComplement8:
            {
                var s = 0;
                foreach (var b in data) s += b;
                return [(byte)((-s) & 0xFF)];
            }
            case ChecksumKind.TwosComplement7:
            {
                var s = 0;
                foreach (var b in data) s += b;
                return [(byte)((-(s & 0x7F)) & 0x7F)];
            }
            case ChecksumKind.Crc16Modbus:
            {
                var crc = Crc16Modbus(data);
                return [(byte)(crc & 0xFF), (byte)(crc >> 8)];
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    private static string Format(byte[] bytes) => Convert.ToHexString(bytes);

    private static bool TryParseHex(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 2 != 0) return false;
        var result = new byte[text.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(text.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i])) return false;
        }
        bytes = result;
        return true;
    }
}
