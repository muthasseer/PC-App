namespace Weighbridge.Core.Indicators.Learning;

public sealed record LineSettingsCandidate(int BaudRate, int DataBits, ParityKind Parity, StopBitsKind StopBits)
{
    public override string ToString() => $"{BaudRate} {DataBits}{Parity.ToString()[0]}{(StopBits == StopBitsKind.Two ? "2" : "1")}";
}

public sealed record LineScore(LineSettingsCandidate Settings, int Bytes, double PrintableRatio, double DigitRatio, bool HasDelimiters, double Score)
{
    public bool LooksValid => Score >= 0.6;
}

/// <summary>
/// Scores a short sample received with one set of serial parameters. With the wrong baud rate or parity the
/// UART produces mostly non-printable bytes, so the setting with clean ASCII digits and regular delimiters wins.
/// </summary>
public static class LineQualityScorer
{
    public static readonly int[] CommonBaudRates = [9600, 4800, 2400, 1200, 19200, 38400, 57600, 115200];

    public static IEnumerable<LineSettingsCandidate> DefaultScanOrder()
    {
        foreach (var baud in CommonBaudRates)
        {
            yield return new LineSettingsCandidate(baud, 8, ParityKind.None, StopBitsKind.One);
            yield return new LineSettingsCandidate(baud, 7, ParityKind.Even, StopBitsKind.One);
            yield return new LineSettingsCandidate(baud, 7, ParityKind.Odd, StopBitsKind.One);
        }
    }

    public static LineScore Score(LineSettingsCandidate settings, ReadOnlySpan<byte> data, int uartErrors = 0)
    {
        if (data.Length == 0) return new LineScore(settings, 0, 0, 0, false, 0);

        int printable = 0, digits = 0, delimiters = 0;
        foreach (var b in data)
        {
            if (b is >= 0x20 and < 0x7F) printable++;
            if (b is >= 0x30 and <= 0x39) digits++;
            if (b is 0x0D or 0x0A or 0x02 or 0x03) delimiters++;
        }
        var printableRatio = (printable + delimiters) / (double)data.Length;
        var digitRatio = digits / (double)data.Length;
        var hasDelimiters = delimiters >= 2;

        var score = printableRatio * 0.6
                    + Math.Min(digitRatio * 2, 1) * 0.2
                    + (hasDelimiters ? 0.2 : 0)
                    - Math.Min(0.5, uartErrors * 0.05);
        if (data.Length < 8) score *= 0.5;
        return new LineScore(settings, data.Length, printableRatio, digitRatio, hasDelimiters, Math.Clamp(score, 0, 1));
    }
}
