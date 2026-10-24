using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Indicators.Learning;

public enum SampleTokenKind { Number, Word, Space, Symbol }

public sealed record SampleToken(SampleTokenKind Kind, string Text);

public sealed record WeightCandidate(
    int NumberIndex,
    int TokenIndex,
    string LatestExample,
    decimal LatestValue,
    int DistinctValues,
    bool HasDecimal,
    bool HasSign,
    string? FollowingUnit,
    double Score,
    string Description);

public sealed record StatusCandidate(int TokenIndex, IReadOnlyList<string> Values, IReadOnlyDictionary<string, ReadingFlags> KnownMeanings, IReadOnlyList<string> UnknownValues);

public sealed record FormatAnalysis(
    int FrameCount,
    string DominantShape,
    double ShapeCoverage,
    IReadOnlyList<WeightCandidate> WeightCandidates,
    IReadOnlyList<StatusCandidate> StatusCandidates,
    WeightUnit? DetectedUnit,
    ParserOptions SuggestedParser,
    double SuggestedParseRate,
    IReadOnlyList<string> Notes);

public sealed record ReferenceMatch(ParserOptions Parser, string Description, decimal ParsedValue);

/// <summary>
/// Learns an unknown ASCII output format from sample frames: finds the message shape, ranks the numbers that could
/// be the weight, recognises unit and status words, and generates a parser that is then verified against the
/// samples and against a reference weight the operator reads off the indicator display.
/// </summary>
public static class FormatLearner
{
    public static IReadOnlyList<SampleToken> Tokenize(string text)
    {
        var tokens = new List<SampleToken>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var start = i;
            if (char.IsAsciiDigit(c) || ((c is '+' or '-') && NextIsDigitAfterSpaces(text, i)))
            {
                i++;
                // A sign may be separated from its digits by padding spaces ("-   1234").
                while (i < text.Length && text[i] == ' ' && (c is '+' or '-')) i++;
                var seenSep = false;
                while (i < text.Length)
                {
                    if (char.IsAsciiDigit(text[i])) { i++; continue; }
                    if (!seenSep && text[i] is '.' or ',' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])) { seenSep = true; i++; continue; }
                    break;
                }
                tokens.Add(new SampleToken(SampleTokenKind.Number, text[start..i]));
            }
            else if (char.IsAsciiLetter(c))
            {
                while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                tokens.Add(new SampleToken(SampleTokenKind.Word, text[start..i]));
            }
            else if (c == ' ' || c == '\t')
            {
                while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                tokens.Add(new SampleToken(SampleTokenKind.Space, " "));
            }
            else
            {
                tokens.Add(new SampleToken(SampleTokenKind.Symbol, c.ToString()));
                i++;
            }
        }
        return tokens;
    }

    public static string Shape(IReadOnlyList<SampleToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            sb.Append(t.Kind switch
            {
                SampleTokenKind.Number => "N",
                SampleTokenKind.Word => "W",
                SampleTokenKind.Space => "_",
                _ => t.Text,
            });
        }
        return sb.ToString();
    }

    public static FormatAnalysis Analyze(IReadOnlyList<string> frames)
    {
        var notes = new List<string>();
        var texts = frames.Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        if (texts.Count == 0)
        {
            return new FormatAnalysis(0, string.Empty, 0, [], [], null, new ParserOptions(), 0, ["No data received yet."]);
        }

        var tokenized = texts.Select(t => (Text: t, Tokens: Tokenize(t))).ToList();
        var groups = tokenized.GroupBy(t => Shape(t.Tokens)).OrderByDescending(g => g.Count()).ToList();
        var dominant = groups[0].ToList();
        var shape = groups[0].Key;
        var coverage = dominant.Count / (double)texts.Count;
        if (coverage < 0.8) notes.Add($"Only {coverage:P0} of the messages share the same layout; the indicator may send several message types.");

        var template = dominant[^1].Tokens;
        var numberIndexes = template.Select((t, i) => (t, i)).Where(x => x.t.Kind == SampleTokenKind.Number).Select(x => x.i).ToList();
        var wordIndexes = template.Select((t, i) => (t, i)).Where(x => x.t.Kind == SampleTokenKind.Word).Select(x => x.i).ToList();

        // ---- Units and status words
        WeightUnit? detectedUnit = null;
        int? unitTokenIndex = null;
        var statusCandidates = new List<StatusCandidate>();
        foreach (var wi in wordIndexes)
        {
            var values = dominant.Select(d => d.Tokens[wi].Text).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 1 && WeightUnits.TryParse(values[0], out var unit) && PrecededByNumber(template, wi))
            {
                detectedUnit = unit;
                unitTokenIndex = wi;
                continue;
            }
            var known = new Dictionary<string, ReadingFlags>(StringComparer.OrdinalIgnoreCase);
            var unknown = new List<string>();
            foreach (var v in values)
            {
                var flags = ParserOptions.DefaultStatusRules.Where(r => r.Token.Equals(v, StringComparison.OrdinalIgnoreCase)).Aggregate(ReadingFlags.None, (a, r) => a | r.Flags);
                if (flags != ReadingFlags.None) known[v] = flags;
                else unknown.Add(v);
            }
            if (known.Count > 0 || values.Count > 1) statusCandidates.Add(new StatusCandidate(wi, values, known, unknown));
        }

        // ---- Weight candidates
        var candidates = new List<WeightCandidate>();
        for (var n = 0; n < numberIndexes.Count; n++)
        {
            var ti = numberIndexes[n];
            var raw = dominant.Select(d => d.Tokens[ti].Text).ToList();
            var values = raw.Select(ParseLoose).ToList();
            var distinct = values.Distinct().Count();
            var latest = raw[^1];
            var digitCount = latest.Count(char.IsAsciiDigit);
            var hasDecimal = latest.Contains('.') || latest.Contains(',');
            var hasSign = latest.TrimStart().StartsWith('+') || latest.TrimStart().StartsWith('-');
            var followingUnit = FollowingWord(template, ti, out var fw) && WeightUnits.TryParse(fw, out _) ? fw : null;

            var score = 1.0;
            var reasons = new List<string>();
            if (followingUnit is not null) { score += 2; reasons.Add($"followed by unit '{followingUnit}'"); }
            if (hasSign) { score += 1; reasons.Add("signed"); }
            if (hasDecimal) { score += 0.5; reasons.Add("has a decimal point"); }
            score += Math.Min(3, Math.Max(0, digitCount - 3));
            if (distinct > 1) { score += 1.5; reasons.Add("changes with the load"); }
            if (digitCount <= 2) { score -= 2; reasons.Add("very few digits (address or code?)"); }
            if (IsCounter(values)) { score -= 3; reasons.Add("increments like a counter"); }

            candidates.Add(new WeightCandidate(n, ti, latest.Trim(), ParseLoose(latest), distinct, hasDecimal, hasSign, followingUnit, score,
                reasons.Count > 0 ? string.Join(", ", reasons) : "number"));
        }
        candidates = candidates.OrderByDescending(c => c.Score).ToList();
        if (candidates.Count == 0) notes.Add("No numbers were found in the messages. Check the baud rate and parity, or the indicator may need a request command.");

        // ---- Suggested parser
        var parser = BuildSuggestion(template, dominant.Select(d => d.Tokens).ToList(), candidates.FirstOrDefault(), unitTokenIndex, detectedUnit, statusCandidates, notes);
        var parseRate = ParseRate(parser, texts);

        // Prefer the simpler Auto mode when it gives the same result on every sample.
        if (candidates.Count == 1)
        {
            var auto = new ParserOptions
            {
                DefaultUnit = detectedUnit ?? WeightUnit.Kilogram,
                AllowSingleLetterUnits = detectedUnit is not null && template[unitTokenIndex!.Value].Text.Length == 1,
                DecimalSeparator = parser.DecimalSeparator,
                StatusRules = parser.StatusRules,
            };
            var autoRate = ParseRate(auto, texts);
            if (autoRate >= parseRate && autoRate >= 0.95) (parser, parseRate) = (auto, autoRate);
        }

        foreach (var s in statusCandidates.Where(s => s.UnknownValues.Count > 0))
        {
            notes.Add($"Unknown status code(s) {string.Join(", ", s.UnknownValues.Select(v => "'" + v + "'"))}: assign their meaning (stable, motion, overload…) in the status table.");
        }
        if (statusCandidates.All(s => !s.KnownMeanings.Values.Any(f => (f & (ReadingFlags.Stable | ReadingFlags.Motion)) != 0)))
        {
            notes.Add("The indicator does not seem to report stable/motion. Use the software stability check (mode: Software).");
        }

        return new FormatAnalysis(texts.Count, shape, coverage, candidates, statusCandidates, detectedUnit, parser, parseRate, notes);
    }

    /// <summary>
    /// The operator reads the indicator display (e.g. "12340 kg"). Returns the parser variants (weight field,
    /// implied decimals, reversed digits, multiplier) whose result equals that value for the latest frame.
    /// </summary>
    public static IReadOnlyList<ReferenceMatch> MatchReference(FormatAnalysis analysis, string latestFrame, decimal displayedValue, WeightUnit displayedUnit)
    {
        var matches = new List<ReferenceMatch>();
        var bases = new List<(ParserOptions Options, string Label)> { (analysis.SuggestedParser, "suggested field") };
        var tokens = Tokenize(latestFrame.Trim());
        foreach (var c in analysis.WeightCandidates.Skip(1))
        {
            var alt = BuildSuggestion(tokens, [tokens], c, null, analysis.DetectedUnit, analysis.StatusCandidates, []);
            bases.Add((alt, $"number #{c.NumberIndex + 1} ({c.LatestExample})"));
        }

        var target = Weight.From(displayedValue, displayedUnit).Kilograms;
        foreach (var (options, label) in bases)
        {
            foreach (var (variant, desc) in Variants(options))
            {
                ParseResult result;
                try { result = new WeightParser(variant).Parse(RawFrame.FromText(latestFrame, DateTimeOffset.UtcNow)); }
                catch (ArgumentException) { continue; }
                if (result.Reading?.Weight is not { } w) continue;
                if (w.Kilograms == target || (result.Reading.Value is { } v && Weight.From(v, displayedUnit).Kilograms == target))
                {
                    var chosen = variant;
                    if (w.Kilograms != target) chosen = variant with { DefaultUnit = displayedUnit };
                    matches.Add(new ReferenceMatch(chosen, $"{label}{desc}", result.Reading.Value ?? 0));
                }
            }
        }
        return matches;
    }

    private static IEnumerable<(ParserOptions, string)> Variants(ParserOptions o)
    {
        yield return (o, string.Empty);
        for (var d = 1; d <= 3; d++) yield return (o with { ImpliedDecimals = d }, $", {d} implied decimal(s)");
        yield return (o with { ReverseDigits = true }, ", reversed digits");
        yield return (o with { Multiplier = 10 }, ", ×10");
        yield return (o with { Multiplier = 100 }, ", ×100");
    }

    private static ParserOptions BuildSuggestion(
        IReadOnlyList<SampleToken> template,
        IReadOnlyList<IReadOnlyList<SampleToken>> samples,
        WeightCandidate? weight,
        int? unitTokenIndex,
        WeightUnit? unit,
        IReadOnlyList<StatusCandidate> statusCandidates,
        List<string> notes)
    {
        var commaDecimal = weight is not null && weight.LatestExample.Contains(',') && !weight.LatestExample.Contains('.');
        if (weight is null) return new ParserOptions();

        var statusIndexes = statusCandidates.Select(s => s.TokenIndex).ToHashSet();
        var sb = new StringBuilder("^");
        for (var i = 0; i < template.Count; i++)
        {
            var t = template[i];
            switch (t.Kind)
            {
                case SampleTokenKind.Number when i == weight.TokenIndex:
                    sb.Append(@"(?<weight>[+-]?\s*\d+(?:[.,]\d+)?)");
                    break;
                case SampleTokenKind.Number:
                    sb.Append(@"[+-]?\s*\d+(?:[.,]\d+)?");
                    break;
                case SampleTokenKind.Word when i == unitTokenIndex:
                    sb.Append("(?<unit>").Append(Regex.Escape(t.Text)).Append(')');
                    break;
                case SampleTokenKind.Word when statusIndexes.Contains(i):
                    sb.Append(@"(?<status>[A-Za-z]+)");
                    break;
                case SampleTokenKind.Word:
                    var constant = samples.All(s => s.Count > i && s[i].Text == t.Text);
                    sb.Append(constant ? Regex.Escape(t.Text) : "[A-Za-z]+");
                    break;
                case SampleTokenKind.Space:
                    sb.Append(@"\s*");
                    break;
                default:
                    sb.Append(Regex.Escape(t.Text));
                    break;
            }
        }
        sb.Append('$');

        var rules = statusCandidates.SelectMany(s => s.KnownMeanings.Select(k => new StatusRule(k.Key, k.Value))).DistinctBy(r => r.Token.ToUpperInvariant()).ToList();
        return new ParserOptions
        {
            Mode = ParserMode.Regex,
            Pattern = sb.ToString(),
            DefaultUnit = unit ?? WeightUnit.Kilogram,
            AllowSingleLetterUnits = unitTokenIndex is { } ui && template[ui].Text.Length == 1,
            DecimalSeparator = commaDecimal ? DecimalSeparatorKind.Comma : DecimalSeparatorKind.Point,
            StatusRules = rules,
            UseDefaultStatusRules = true,
        };
    }

    private static double ParseRate(ParserOptions options, IReadOnlyList<string> texts)
    {
        WeightParser parser;
        try { parser = new WeightParser(options); }
        catch (ArgumentException) { return 0; }
        var ok = texts.Count(t => parser.Parse(RawFrame.FromText(t, DateTimeOffset.UtcNow)).Outcome == ParseOutcome.Parsed);
        return ok / (double)texts.Count;
    }

    private static bool NextIsDigitAfterSpaces(string text, int i)
    {
        var k = i + 1;
        while (k < text.Length && text[k] == ' ') k++;
        return k < text.Length && char.IsAsciiDigit(text[k]);
    }

    private static bool PrecededByNumber(IReadOnlyList<SampleToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == SampleTokenKind.Space) continue;
            return tokens[i].Kind == SampleTokenKind.Number;
        }
        return false;
    }

    private static bool FollowingWord(IReadOnlyList<SampleToken> tokens, int index, out string word)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SampleTokenKind.Space) continue;
            if (tokens[i].Kind == SampleTokenKind.Word)
            {
                word = tokens[i].Text;
                return true;
            }
            break;
        }
        word = string.Empty;
        return false;
    }

    private static decimal ParseLoose(string token)
    {
        var t = token.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static bool IsCounter(List<decimal> values)
    {
        if (values.Count < 4) return false;
        var increments = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] - values[i - 1] == 1) increments++;
        }
        return increments >= values.Count - 2;
    }
}
