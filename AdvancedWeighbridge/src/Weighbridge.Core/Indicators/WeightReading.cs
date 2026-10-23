using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Indicators;

[Flags]
public enum ReadingFlags
{
    None = 0,

    /// <summary>The indicator itself reports a stable (no motion) weight.</summary>
    Stable = 1 << 0,

    /// <summary>The indicator reports motion / unstable weight.</summary>
    Motion = 1 << 1,

    Overload = 1 << 2,
    Underload = 1 << 3,
    Error = 1 << 4,
    Gross = 1 << 5,
    Net = 1 << 6,
    Tare = 1 << 7,
    CenterOfZero = 1 << 8,
}

/// <summary>A successfully parsed indicator frame.</summary>
public sealed record WeightReading
{
    /// <summary>Numeric value in <see cref="Unit"/>; null when the indicator reports overload/error without a number.</summary>
    public decimal? Value { get; init; }

    public WeightUnit Unit { get; init; } = WeightUnit.Kilogram;

    public ReadingFlags Flags { get; init; }

    /// <summary>Number of decimals the indicator transmitted (keeps the display faithful to the indicator).</summary>
    public int ReportedDecimals { get; init; }

    /// <summary>The decoded frame text the value was parsed from.</summary>
    public string Text { get; init; } = string.Empty;

    public RawFrame? Frame { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public long Sequence { get; init; }

    public string? ErrorCode { get; init; }

    public Weight? Weight => Value is { } v ? Units.Weight.From(v, Unit) : null;

    /// <summary>True/false when the indicator reports stability, null when it does not say.</summary>
    public bool? IndicatorStable =>
        (Flags & ReadingFlags.Motion) != 0 ? false :
        (Flags & ReadingFlags.Stable) != 0 ? true :
        null;

    public bool IsOverload => (Flags & (ReadingFlags.Overload | ReadingFlags.Underload)) != 0;
    public bool IsError => (Flags & ReadingFlags.Error) != 0;

    /// <summary>A reading that carries a usable number and no overload/error condition.</summary>
    public bool IsUsable => Value is not null && !IsOverload && !IsError;
}

public enum ParseOutcome
{
    Parsed = 0,

    /// <summary>The frame is deliberately ignored (header line, blank line, matches the ignore pattern).</summary>
    Ignored = 1,

    /// <summary>The frame is not a trustworthy weight (malformed, ambiguous, failed checksum, …).</summary>
    Rejected = 2,
}

public sealed record ParseResult(ParseOutcome Outcome, WeightReading? Reading, string? Reason, RawFrame? Frame)
{
    public static ParseResult Ok(WeightReading reading) => new(ParseOutcome.Parsed, reading, null, reading.Frame);
    public static ParseResult Ignore(RawFrame? frame, string reason) => new(ParseOutcome.Ignored, null, reason, frame);
    public static ParseResult Reject(RawFrame? frame, string reason) => new(ParseOutcome.Rejected, null, reason, frame);
}
