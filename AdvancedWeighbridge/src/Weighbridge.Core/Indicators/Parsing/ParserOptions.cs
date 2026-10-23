using Weighbridge.Core.Units;

namespace Weighbridge.Core.Indicators.Parsing;

public enum ParserMode
{
    /// <summary>Finds exactly one number in the frame (plus optional unit and status words). Rejects ambiguous frames.</summary>
    Auto = 0,

    /// <summary>A regular expression with named groups: <c>weight</c> (required), <c>sign</c>, <c>unit</c>, <c>status</c>,
    /// and flag groups <c>stable</c>, <c>motion</c>, <c>overload</c>, <c>error</c>, <c>gross</c>, <c>net</c>.</summary>
    Regex = 1,

    /// <summary>Fields separated by a delimiter; weight/unit/status taken from configured field indexes.</summary>
    Delimited = 2,

    /// <summary>Fields at fixed character positions.</summary>
    FixedPosition = 3,

    /// <summary>A built-in or plug-in protocol decoder (binary formats, status bytes, proprietary encodings).</summary>
    Protocol = 4,
}

public enum TextEncodingKind
{
    Ascii = 0,
    Latin1 = 1,
    Utf8 = 2,
}

public enum DecimalSeparatorKind
{
    Point = 0,
    Comma = 1,
}

public enum ThousandsSeparatorKind
{
    None = 0,
    Comma = 1,
    Point = 2,
    Space = 3,
    Apostrophe = 4,
}

public enum StatusMatch
{
    /// <summary>The token appears as a whole word (delimited by non-alphanumeric characters).</summary>
    Word = 0,

    /// <summary>The token appears anywhere.</summary>
    Contains = 1,

    /// <summary>The token is a regular expression.</summary>
    Regex = 2,
}

/// <summary>Maps an indicator status code (e.g. <c>ST</c>, <c>US</c>, <c>OL</c>, <c>GS</c>, <c>NT</c>) to reading flags.</summary>
public sealed record StatusRule(string Token, ReadingFlags Flags, StatusMatch Match = StatusMatch.Word, bool CaseSensitive = false);

public sealed record FieldSpan(int Start, int Length);

public sealed record ParserOptions
{
    public ParserMode Mode { get; init; } = ParserMode.Auto;
    public TextEncodingKind Encoding { get; init; } = TextEncodingKind.Ascii;

    // --- Regex mode
    public string? Pattern { get; init; }

    // --- Delimited mode
    public string Delimiter { get; init; } = ",";

    /// <summary>Zero-based field holding the weight; -1 = the single numeric field (ambiguity is rejected).</summary>
    public int WeightField { get; init; } = -1;

    public int UnitField { get; init; } = -1;

    /// <summary>Fields scanned for status codes; empty = every field except the weight field.</summary>
    public IReadOnlyList<int> StatusFields { get; init; } = [];

    // --- Fixed position mode
    public FieldSpan? WeightSpan { get; init; }
    public FieldSpan? SignSpan { get; init; }
    public FieldSpan? UnitSpan { get; init; }
    public IReadOnlyList<FieldSpan> StatusSpans { get; init; } = [];

    // --- Auto mode
    /// <summary>When the frame holds several numbers, the zero-based index of the weight. -1 = reject as ambiguous.</summary>
    public int NumberIndex { get; init; } = -1;

    // --- Protocol mode
    public string? ProtocolName { get; init; }
    public IReadOnlyDictionary<string, string> ProtocolSettings { get; init; } = new Dictionary<string, string>();

    // --- Number format
    public DecimalSeparatorKind DecimalSeparator { get; init; } = DecimalSeparatorKind.Point;
    public ThousandsSeparatorKind ThousandsSeparator { get; init; } = ThousandsSeparatorKind.None;

    /// <summary>Digits after an implied decimal point when the indicator sends none (e.g. "012345" with 1 → 1234.5).</summary>
    public int ImpliedDecimals { get; init; }

    /// <summary>Some indicators (e.g. XK3190-A9 in continuous mode) transmit the number reversed: "=00.0210" means 0120.00.</summary>
    public bool ReverseDigits { get; init; }

    /// <summary>Applied after parsing, e.g. 10 when the indicator sends the value in tens of kilograms.</summary>
    public decimal Multiplier { get; init; } = 1m;

    public int MaxDigits { get; init; } = 9;
    public bool AllowNegative { get; init; } = true;

    // --- Units
    public WeightUnit DefaultUnit { get; init; } = WeightUnit.Kilogram;
    public bool RequireUnit { get; init; }

    /// <summary>Accept "t" / "g" directly after the number as units. Off by default because single letters are
    /// often status codes (G = gross); an unexpected single letter is then rejected instead of guessed.</summary>
    public bool AllowSingleLetterUnits { get; init; }

    // --- Status
    public IReadOnlyList<StatusRule> StatusRules { get; init; } = [];

    /// <summary>Also apply the built-in codes (ST, US, OL, GS, NT, …).</summary>
    public bool UseDefaultStatusRules { get; init; } = true;

    // --- Frame validation
    /// <summary>Frames must match this regular expression to be accepted.</summary>
    public string? ValidationPattern { get; init; }

    /// <summary>Frames matching this regular expression are skipped silently (headers, banners).</summary>
    public string? IgnorePattern { get; init; }

    public bool RejectNonPrintable { get; init; } = true;
    public int MinLength { get; init; } = 1;
    public int MaxLength { get; init; } = 200;

    /// <summary>Readings beyond this absolute value (in kg) are rejected as implausible (noise, wrong framing).</summary>
    public decimal MaxPlausibleKg { get; init; } = 1_000_000m;

    public static IReadOnlyList<StatusRule> DefaultStatusRules { get; } =
    [
        new("ST", ReadingFlags.Stable),
        new("STA", ReadingFlags.Stable),
        new("STABLE", ReadingFlags.Stable),
        new("US", ReadingFlags.Motion),
        new("UNST", ReadingFlags.Motion),
        new("UNSTABLE", ReadingFlags.Motion),
        new("MO", ReadingFlags.Motion),
        new("MOTION", ReadingFlags.Motion),
        new("OL", ReadingFlags.Overload),
        new("OVER", ReadingFlags.Overload),
        new("OVERLOAD", ReadingFlags.Overload),
        new("UL", ReadingFlags.Underload),
        new("UNDER", ReadingFlags.Underload),
        new("ERR", ReadingFlags.Error),
        new("ERROR", ReadingFlags.Error),
        new("GS", ReadingFlags.Gross),
        new("GROSS", ReadingFlags.Gross),
        new("NT", ReadingFlags.Net),
        new("NET", ReadingFlags.Net),
        new("TR", ReadingFlags.Tare),
        new("TARE", ReadingFlags.Tare),
    ];

    public IEnumerable<StatusRule> EffectiveStatusRules =>
        UseDefaultStatusRules ? StatusRules.Concat(DefaultStatusRules) : StatusRules;
}
