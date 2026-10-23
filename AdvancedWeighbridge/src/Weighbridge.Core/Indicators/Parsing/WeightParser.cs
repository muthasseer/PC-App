using System.Text;
using System.Text.RegularExpressions;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Indicators.Parsing;

public interface IWeightParser
{
    ParseResult Parse(RawFrame frame);
}

/// <summary>
/// Configurable parser that turns one frame into a <see cref="WeightReading"/> or a rejection with a reason.
/// Principle: when in doubt, reject. A rejected frame never becomes a weight on screen or in a transaction.
/// </summary>
public sealed class WeightParser : IWeightParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions RxOptions = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private readonly ParserOptions _options;
    private readonly NumberScanner _scanner;
    private readonly Regex? _pattern;
    private readonly Regex? _validation;
    private readonly Regex? _ignore;
    private readonly IIndicatorProtocol? _protocol;
    private readonly (StatusRule Rule, Regex? Regex)[] _rules;
    private readonly bool _patternHasStatusGroup;
    private readonly Encoding _encoding;

    public WeightParser(ParserOptions options, ProtocolRegistry? protocols = null)
    {
        _options = options;
        _scanner = new NumberScanner(options);
        _encoding = options.Encoding switch
        {
            TextEncodingKind.Utf8 => new UTF8Encoding(false, false),
            _ => Encoding.Latin1,
        };

        if (!string.IsNullOrWhiteSpace(options.ValidationPattern)) _validation = new Regex(options.ValidationPattern, RxOptions, RegexTimeout);
        if (!string.IsNullOrWhiteSpace(options.IgnorePattern)) _ignore = new Regex(options.IgnorePattern, RxOptions, RegexTimeout);

        switch (options.Mode)
        {
            case ParserMode.Regex:
                if (string.IsNullOrWhiteSpace(options.Pattern)) throw new ArgumentException("Regex mode needs a pattern.", nameof(options));
                _pattern = new Regex(options.Pattern, RxOptions, RegexTimeout);
                if (!_pattern.GetGroupNames().Contains("weight")) throw new ArgumentException("The pattern must contain a (?<weight>…) group.", nameof(options));
                _patternHasStatusGroup = _pattern.GetGroupNames().Any(n => n is "status" or "mode");
                break;
            case ParserMode.FixedPosition when options.WeightSpan is null:
                throw new ArgumentException("Fixed-position mode needs a weight span.", nameof(options));
            case ParserMode.Delimited when string.IsNullOrEmpty(options.Delimiter):
                throw new ArgumentException("Delimited mode needs a delimiter.", nameof(options));
            case ParserMode.Protocol:
                var registry = protocols ?? ProtocolRegistry.Default;
                if (string.IsNullOrWhiteSpace(options.ProtocolName) || !registry.TryGet(options.ProtocolName, out _protocol))
                {
                    throw new ArgumentException($"Unknown indicator protocol '{options.ProtocolName}'.", nameof(options));
                }
                break;
        }

        _rules = options.EffectiveStatusRules
            .Select(r => (r, r.Match == StatusMatch.Regex
                ? new Regex(r.Token, RxOptions | (r.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), RegexTimeout)
                : null))
            .ToArray();
    }

    public ParserOptions Options => _options;

    public ParseResult Parse(RawFrame frame)
    {
        try
        {
            if (_protocol is not null) return Validate(_protocol.Parse(frame, _options), frame);

            // Trailing NUL padding is common on fixed-width indicators; it is not a fault.
            var raw = _encoding.GetString(frame.Payload).TrimEnd('\0');
            if (_options.RejectNonPrintable && ContainsNonPrintable(raw, frame.Payload.AsSpan(0, Math.Min(frame.Payload.Length, raw.Length))))
            {
                return ParseResult.Reject(frame, "Frame contains control or non-ASCII bytes (wrong baud rate, parity, or line noise?).");
            }

            var text = raw.Trim(' ', '\0', '\t');
            if (text.Length == 0) return ParseResult.Ignore(frame, "Empty frame.");
            if (_ignore is not null && _ignore.IsMatch(text)) return ParseResult.Ignore(frame, "Frame matches the ignore pattern.");
            if (text.Length < _options.MinLength) return ParseResult.Reject(frame, $"Frame is shorter than {_options.MinLength} characters.");
            if (text.Length > _options.MaxLength) return ParseResult.Reject(frame, $"Frame is longer than {_options.MaxLength} characters.");
            if (_validation is not null && !_validation.IsMatch(text)) return ParseResult.Reject(frame, "Frame does not match the validation pattern.");

            var result = _options.Mode switch
            {
                ParserMode.Auto => ParseAuto(frame, text),
                ParserMode.Regex => ParseRegex(frame, text),
                ParserMode.Delimited => ParseDelimited(frame, text),
                ParserMode.FixedPosition => ParseFixed(frame, raw),
                _ => ParseResult.Reject(frame, $"Unsupported parser mode {_options.Mode}."),
            };
            return Validate(result, frame);
        }
        catch (RegexMatchTimeoutException)
        {
            return ParseResult.Reject(frame, "Pattern evaluation timed out.");
        }
    }

    // ------------------------------------------------------------------ modes

    private ParseResult ParseAuto(RawFrame frame, string text)
    {
        if (!_scanner.TryScan(text, out var tokens, out var error)) return ParseResult.Reject(frame, error!);
        var flags = MatchStatus(text);

        if (tokens.Count == 0)
        {
            return HasFault(flags)
                ? ParseResult.Ok(NewReading(frame, text, null, _options.DefaultUnit, flags, 0))
                : ParseResult.Reject(frame, "No weight value in the frame.");
        }

        NumberToken token;
        if (tokens.Count == 1) token = tokens[0];
        else if (_options.NumberIndex >= 0 && _options.NumberIndex < tokens.Count) token = tokens[_options.NumberIndex];
        else
        {
            return ParseResult.Reject(frame,
                $"Ambiguous frame: {tokens.Count} numbers found ({string.Join(", ", tokens.Select(t => t.Text.Trim()))}). Configure which one is the weight.");
        }

        var unitResult = ResolveUnit(text, token);
        if (unitResult.Error is not null) return ParseResult.Reject(frame, unitResult.Error);
        return Build(frame, text, token, unitResult.Unit, unitResult.Found, flags);
    }

    private ParseResult ParseRegex(RawFrame frame, string text)
    {
        var m = _pattern!.Match(text);
        if (!m.Success) return ParseResult.Reject(frame, "Frame does not match the configured pattern.");

        var flags = ReadingFlags.None;
        if (_patternHasStatusGroup)
        {
            foreach (Capture c in m.Groups["status"].Captures) flags |= MatchStatus(c.Value);
            foreach (Capture c in m.Groups["mode"].Captures) flags |= MatchStatus(c.Value);
        }
        else
        {
            flags |= MatchStatus(text);
        }
        flags |= FlagGroup(m, "stable", ReadingFlags.Stable) | FlagGroup(m, "motion", ReadingFlags.Motion)
               | FlagGroup(m, "overload", ReadingFlags.Overload) | FlagGroup(m, "error", ReadingFlags.Error)
               | FlagGroup(m, "gross", ReadingFlags.Gross) | FlagGroup(m, "net", ReadingFlags.Net);

        var weightGroup = m.Groups["weight"];
        if (!weightGroup.Success || string.IsNullOrWhiteSpace(weightGroup.Value))
        {
            return HasFault(flags)
                ? ParseResult.Ok(NewReading(frame, text, null, _options.DefaultUnit, flags, 0))
                : ParseResult.Reject(frame, "The weight group is empty.");
        }

        if (!_scanner.TryParseSingle(weightGroup.Value, out var token, out var error, out var suffix)) return ParseResult.Reject(frame, error!);

        var unit = _options.DefaultUnit;
        var unitFound = false;
        var unitGroup = m.Groups["unit"];
        if (unitGroup.Success && !string.IsNullOrWhiteSpace(unitGroup.Value))
        {
            if (!WeightUnits.TryParse(unitGroup.Value, out unit)) return ParseResult.Reject(frame, $"Unknown unit '{unitGroup.Value}'.");
            unitFound = true;
        }
        else if (suffix.Length > 0)
        {
            if (!WeightUnits.TryParse(suffix, out unit)) return ParseResult.Reject(frame, $"Unexpected text '{suffix}' after the weight.");
            unitFound = true;
        }

        var signGroup = m.Groups["sign"];
        if (signGroup.Success && signGroup.Value.Trim() == "-" && token!.Value > 0)
        {
            token = token with { Value = -token.Value, HadExplicitSign = true };
        }

        return Build(frame, text, token!, unit, unitFound, flags);
    }

    private ParseResult ParseDelimited(RawFrame frame, string text)
    {
        var delimiter = Encoding.Latin1.GetString(Common.ByteText.Unescape(_options.Delimiter));
        var fields = text.Split(delimiter);
        int weightIndex;
        NumberToken? token;
        string suffix;

        if (_options.WeightField >= 0)
        {
            if (_options.WeightField >= fields.Length) return ParseResult.Reject(frame, $"Frame has {fields.Length} fields; weight field {_options.WeightField + 1} is missing.");
            weightIndex = _options.WeightField;
            if (!_scanner.TryParseSingle(fields[weightIndex].Trim(), out token, out var error, out suffix)) return ParseResult.Reject(frame, error!);
        }
        else
        {
            var candidates = new List<(int Index, NumberToken Token, string Suffix)>();
            for (var i = 0; i < fields.Length; i++)
            {
                if (_scanner.TryParseSingle(fields[i].Trim(), out var t, out _, out var s)) candidates.Add((i, t!, s));
            }
            if (candidates.Count != 1)
            {
                return ParseResult.Reject(frame, candidates.Count == 0
                    ? "No numeric field in the frame."
                    : $"Ambiguous frame: fields {string.Join(", ", candidates.Select(c => c.Index + 1))} are numeric. Configure the weight field.");
            }
            (weightIndex, token, suffix) = candidates[0];
        }

        var unit = _options.DefaultUnit;
        var unitFound = false;
        if (_options.UnitField >= 0 && _options.UnitField < fields.Length && !string.IsNullOrWhiteSpace(fields[_options.UnitField]))
        {
            if (!WeightUnits.TryParse(fields[_options.UnitField], out unit)) return ParseResult.Reject(frame, $"Unknown unit '{fields[_options.UnitField].Trim()}'.");
            unitFound = true;
        }

        var flags = ReadingFlags.None;
        if (suffix.Length > 0)
        {
            var suffixFlags = MatchStatus(suffix);
            var (sUnit, sFound, sError) = SplitUnitSuffix(suffix);
            if (sError is not null && suffixFlags == ReadingFlags.None) return ParseResult.Reject(frame, sError);
            if (sFound)
            {
                if (unitFound && sUnit != unit) return ParseResult.Reject(frame, "Conflicting units in the frame.");
                unit = sUnit;
                unitFound = true;
            }
            flags |= suffixFlags;
        }

        var statusIndexes = _options.StatusFields.Count > 0
            ? _options.StatusFields
            : Enumerable.Range(0, fields.Length).Where(i => i != weightIndex && i != _options.UnitField).ToList();
        foreach (var i in statusIndexes)
        {
            if (i >= 0 && i < fields.Length) flags |= MatchStatus(fields[i].Trim());
        }

        return Build(frame, text, token!, unit, unitFound, flags);
    }

    private ParseResult ParseFixed(RawFrame frame, string raw)
    {
        var span = _options.WeightSpan!;
        if (raw.Length < span.Start + span.Length) return ParseResult.Reject(frame, $"Frame is {raw.Length} characters; the weight field needs {span.Start + span.Length}.");

        var weightText = raw.Substring(span.Start, span.Length);
        if (!_scanner.TryParseSingle(weightText.Trim(), out var token, out var error, out var suffix)) return ParseResult.Reject(frame, error!);
        if (suffix.Length > 0) return ParseResult.Reject(frame, $"Unexpected text '{suffix}' in the weight field.");

        if (_options.SignSpan is { } signSpan)
        {
            if (raw.Length < signSpan.Start + signSpan.Length) return ParseResult.Reject(frame, "Frame is too short for the sign field.");
            var sign = raw.Substring(signSpan.Start, signSpan.Length).Trim();
            if (sign == "-") token = token! with { Value = -Math.Abs(token.Value), HadExplicitSign = true };
            else if (sign is not ("" or "+" or "0")) return ParseResult.Reject(frame, $"Unexpected sign character '{sign}'.");
        }

        var unit = _options.DefaultUnit;
        var unitFound = false;
        if (_options.UnitSpan is { } unitSpan && raw.Length >= unitSpan.Start + unitSpan.Length)
        {
            var unitText = raw.Substring(unitSpan.Start, unitSpan.Length).Trim();
            if (unitText.Length > 0)
            {
                if (!WeightUnits.TryParse(unitText, out unit)) return ParseResult.Reject(frame, $"Unknown unit '{unitText}'.");
                unitFound = true;
            }
        }

        var flags = ReadingFlags.None;
        foreach (var s in _options.StatusSpans)
        {
            if (raw.Length >= s.Start + s.Length) flags |= MatchStatus(raw.Substring(s.Start, s.Length).Trim());
        }

        return Build(frame, raw.Trim(), token!, unit, unitFound, flags);
    }

    // ------------------------------------------------------------------ helpers

    private ParseResult Build(RawFrame frame, string text, NumberToken token, WeightUnit unit, bool unitFound, ReadingFlags flags)
    {
        if (_options.RequireUnit && !unitFound) return ParseResult.Reject(frame, "The frame has no weight unit but the profile requires one.");

        var value = token.Value;
        var decimals = token.Decimals;
        if (decimals == 0 && _options.ImpliedDecimals > 0)
        {
            value /= Pow10(_options.ImpliedDecimals);
            decimals = _options.ImpliedDecimals;
        }
        if (_options.Multiplier != 1m) value *= _options.Multiplier;

        return ParseResult.Ok(NewReading(frame, text, value, unit, flags, decimals));
    }

    private ParseResult Validate(ParseResult result, RawFrame frame)
    {
        if (result.Outcome != ParseOutcome.Parsed || result.Reading is null) return result;
        var reading = result.Reading;
        if (reading.Value is { } v)
        {
            if (v < 0 && !_options.AllowNegative) return ParseResult.Reject(frame, "Negative weight is not allowed by the profile.");
            var kg = WeightUnits.ToKilograms(Math.Abs(v), reading.Unit);
            if (kg > _options.MaxPlausibleKg) return ParseResult.Reject(frame, $"Implausible value {v} {WeightUnits.Symbol(reading.Unit)} (limit {_options.MaxPlausibleKg} kg).");
        }
        return result;
    }

    private WeightReading NewReading(RawFrame frame, string text, decimal? value, WeightUnit unit, ReadingFlags flags, int decimals) => new()
    {
        Value = value,
        Unit = unit,
        Flags = flags,
        ReportedDecimals = decimals,
        Text = text,
        Frame = frame,
        ReceivedAt = frame.ReceivedAt,
        Sequence = frame.Sequence,
    };

    private readonly record struct UnitResolution(WeightUnit Unit, bool Found, string? Error);

    private UnitResolution ResolveUnit(string text, NumberToken token)
    {
        var units = new HashSet<WeightUnit>();
        var i = token.End;
        while (i < text.Length && text[i] == ' ') i++;
        var runStart = i;
        while (i < text.Length && char.IsAsciiLetter(text[i]) && i - runStart < 12) i++;
        var run = text[runStart..i];

        if (run.Length > 0)
        {
            var (u, found, error) = SplitUnitSuffix(run);
            if (error is not null && MatchStatus(run) == ReadingFlags.None) return new(default, false, error);
            if (found) units.Add(u);
        }

        // A multi-letter unit word elsewhere in the frame ("kg 12500", "Weight 12500 (kg)").
        foreach (var symbol in WeightUnits.KnownSymbols.Where(s => s.Length > 1))
        {
            if (ContainsWord(text, symbol, caseSensitive: false) && WeightUnits.TryParse(symbol, out var u2)) units.Add(u2);
        }

        return units.Count switch
        {
            0 => new(_options.DefaultUnit, false, null),
            1 => new(units.First(), true, null),
            _ => new(default, false, "Conflicting units in the frame."),
        };
    }

    /// <summary>Interprets the letters right after a number: a unit, a unit followed by a status code, or a status code.</summary>
    private (WeightUnit Unit, bool Found, string? Error) SplitUnitSuffix(string suffix)
    {
        var letters = new string(suffix.TakeWhile(char.IsAsciiLetter).ToArray());
        if (letters.Length == 0) return (default, false, null);

        // Longest multi-letter unit prefix wins ("kgs", then "kg").
        for (var len = Math.Min(letters.Length, 8); len >= 2; len--)
        {
            if (WeightUnits.TryParse(letters[..len], out var u)) return (u, true, null);
        }

        if (letters.Length == 1 && WeightUnits.TryParse(letters, out var single))
        {
            if (MatchStatus(letters) != ReadingFlags.None) return (default, false, null);
            if (_options.AllowSingleLetterUnits) return (single, true, null);
            return (default, false, $"Ambiguous single letter '{letters}' after the weight (unit or status?). Enable single-letter units or add a status rule in the profile.");
        }

        return (default, false, null);
    }

    private ReadingFlags MatchStatus(string text)
    {
        var flags = ReadingFlags.None;
        if (string.IsNullOrEmpty(text)) return flags;
        foreach (var (rule, regex) in _rules)
        {
            var hit = rule.Match switch
            {
                StatusMatch.Word => ContainsWord(text, rule.Token, rule.CaseSensitive),
                StatusMatch.Contains => text.Contains(rule.Token, rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
                StatusMatch.Regex => regex!.IsMatch(text),
                _ => false,
            };
            if (hit) flags |= rule.Flags;
        }
        return flags;
    }

    internal static bool ContainsWord(string text, string word, bool caseSensitive)
    {
        if (word.Length == 0) return false;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while ((index = text.IndexOf(word, index, comparison)) >= 0)
        {
            // A digit next to a letter-token is a boundary too: "+0012500G", "12500kg".
            var before = index == 0 || !char.IsAsciiLetterOrDigit(text[index - 1])
                         || (char.IsAsciiDigit(text[index - 1]) && char.IsAsciiLetter(word[0]));
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length || !char.IsAsciiLetterOrDigit(text[afterIndex])
                        || (char.IsAsciiDigit(text[afterIndex]) && char.IsAsciiLetter(word[^1]));
            if (before && after) return true;
            index++;
        }
        return false;
    }

    private static ReadingFlags FlagGroup(Match m, string name, ReadingFlags flag)
    {
        var g = m.Groups[name];
        return g.Success && g.Value.Length > 0 ? flag : ReadingFlags.None;
    }

    private static bool HasFault(ReadingFlags flags) => (flags & (ReadingFlags.Overload | ReadingFlags.Underload | ReadingFlags.Error)) != 0;

    private bool ContainsNonPrintable(string text, ReadOnlySpan<byte> payload)
    {
        if (_options.Encoding == TextEncodingKind.Ascii)
        {
            foreach (var b in payload)
            {
                if (b > 0x7E) return true;
            }
        }
        if (_options.Encoding == TextEncodingKind.Utf8 && text.Contains('�')) return true;
        foreach (var ch in text)
        {
            if ((ch < 0x20 && ch != '\t') || ch == 0x7F) return true;
        }
        return false;
    }

    private static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }
}
