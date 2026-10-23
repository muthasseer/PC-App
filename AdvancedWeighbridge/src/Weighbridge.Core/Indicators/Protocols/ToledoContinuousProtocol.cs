using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Indicators.Protocols;

/// <summary>
/// Mettler Toledo "standard continuous output": <c>STX SWA SWB SWC W1..W6 T1..T6 CR [CHK]</c>.
/// Status word A holds the decimal-point / dummy-zero code, status word B the gross/net, sign, out-of-range,
/// motion and kg/lb bits, status word C the extended unit. Many other brands offer a "Toledo compatible" mode.
/// Implemented from the published format description; confirm against a reference load during setup.
/// </summary>
public sealed class ToledoContinuousProtocol : IIndicatorProtocol
{
    public string Name => "toledo-continuous";
    public string DisplayName => "Mettler Toledo continuous output (and compatibles)";
    public string Description => "STX + 3 status bytes + 6-digit weight + 6-digit tare + CR, optional 7-bit two's-complement checksum.";

    public FramingOptions RecommendedFraming { get; } = new()
    {
        Mode = FramingMode.StartEnd,
        StartMarker = "<STX>",
        EndMarker = "<CR>",
        TrailerLength = 0,
        MaxFrameLength = 32,
    };

    public ChecksumOptions RecommendedChecksum { get; } = new() { Kind = ChecksumKind.None };

    public string? PollCommand => null;

    public ParseResult Parse(RawFrame frame, ParserOptions options)
    {
        var p = frame.Payload;
        if (p.Length < 15) return ParseResult.Reject(frame, $"Toledo frame has {p.Length} bytes between STX and CR (expected 15).");

        byte swa = p[0], swb = p[1], swc = p[2];
        if ((swa & 0x20) == 0 || (swb & 0x20) == 0 || (swc & 0x20) == 0)
        {
            return ParseResult.Reject(frame, "Toledo status bytes are invalid (bit 5 must be set) – check data bits and parity (usually 7E1).");
        }

        var weightText = System.Text.Encoding.ASCII.GetString(p, 3, 6);
        var tareText = System.Text.Encoding.ASCII.GetString(p, 9, 6);
        if (!TryDigits(weightText, out var raw)) return ParseResult.Reject(frame, $"Toledo weight field '{weightText}' is not numeric.");
        if (!TryDigits(tareText, out _)) return ParseResult.Reject(frame, $"Toledo tare field '{tareText}' is not numeric.");

        var (factor, decimals) = (swa & 0x07) switch
        {
            0 => (100m, 0),
            1 => (10m, 0),
            2 => (1m, 0),
            3 => (0.1m, 1),
            4 => (0.01m, 2),
            5 => (0.001m, 3),
            6 => (0.0001m, 4),
            _ => (0.00001m, 5),
        };

        var flags = ReadingFlags.None;
        flags |= (swb & 0x01) != 0 ? ReadingFlags.Net : ReadingFlags.Gross;
        var negative = (swb & 0x02) != 0;
        if ((swb & 0x04) != 0) flags |= negative ? ReadingFlags.Underload : ReadingFlags.Overload;
        flags |= (swb & 0x08) != 0 ? ReadingFlags.Motion : ReadingFlags.Stable;
        string? errorCode = null;
        if ((swb & 0x40) != 0)
        {
            flags |= ReadingFlags.Error;
            errorCode = "POWER-UP";
        }

        WeightUnit unit;
        switch (swc & 0x07)
        {
            case 0:
                unit = (swb & 0x10) != 0 ? WeightUnit.Kilogram : WeightUnit.Pound;
                break;
            case 1:
                unit = WeightUnit.Gram;
                break;
            case 2:
                unit = WeightUnit.Tonne;
                break;
            default:
                return ParseResult.Reject(frame, $"Toledo extended unit code {swc & 0x07} is not supported.");
        }

        var value = raw * factor;
        if (negative) value = -value;

        return ParseResult.Ok(new WeightReading
        {
            Value = value,
            Unit = unit,
            Flags = flags,
            ReportedDecimals = decimals,
            Text = weightText.Trim(),
            Frame = frame,
            ReceivedAt = frame.ReceivedAt,
            Sequence = frame.Sequence,
            ErrorCode = errorCode,
        });
    }

    private static bool TryDigits(string text, out decimal value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true; // blanked leading digits = zero
        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }
        value = decimal.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Builds a frame for simulators and tests.</summary>
    public static byte[] Encode(decimal kilograms, bool motion, bool net = false, bool withChecksum = false, int decimalCode = 2, bool overload = false)
    {
        var factor = decimalCode switch { 0 => 100m, 1 => 10m, 2 => 1m, 3 => 0.1m, 4 => 0.01m, 5 => 0.001m, 6 => 0.0001m, _ => 0.00001m };
        var digits = (long)Math.Round(Math.Abs(kilograms) / factor, MidpointRounding.AwayFromZero);
        var swa = (byte)(0x20 | (decimalCode & 0x07) | 0x08);
        var swb = (byte)(0x20 | 0x10 | (net ? 0x01 : 0) | (kilograms < 0 ? 0x02 : 0) | (overload ? 0x04 : 0) | (motion ? 0x08 : 0));
        byte swc = 0x20;
        var body = new List<byte> { 0x02, swa, swb, swc };
        body.AddRange(System.Text.Encoding.ASCII.GetBytes(Math.Min(digits, 999999).ToString("000000", System.Globalization.CultureInfo.InvariantCulture)));
        body.AddRange(System.Text.Encoding.ASCII.GetBytes("000000"));
        body.Add(0x0D);
        if (withChecksum) body.AddRange(ChecksumValidator.Compute(ChecksumKind.TwosComplement7, body.ToArray()));
        return body.ToArray();
    }
}
