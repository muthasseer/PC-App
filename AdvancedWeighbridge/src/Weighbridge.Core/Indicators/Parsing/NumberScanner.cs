using System.Globalization;
using System.Text;

namespace Weighbridge.Core.Indicators.Parsing;

/// <summary>A number found in frame text.</summary>
public sealed record NumberToken(int Start, int Length, string Text, decimal Value, int Decimals, bool HadExplicitSign, int DigitCount)
{
    public int End => Start + Length;
}

/// <summary>
/// Strict number recognition for indicator frames. A token that looks numeric but is malformed (two decimal
/// separators, broken thousands grouping, too many digits) is reported as an error instead of being partially used.
/// </summary>
public sealed class NumberScanner
{
    private readonly char _decimal;
    private readonly char? _thousands;
    private readonly ParserOptions _options;

    public NumberScanner(ParserOptions options)
    {
        _options = options;
        _decimal = options.DecimalSeparator == DecimalSeparatorKind.Comma ? ',' : '.';
        _thousands = options.ThousandsSeparator switch
        {
            ThousandsSeparatorKind.Comma => ',',
            ThousandsSeparatorKind.Point => '.',
            ThousandsSeparatorKind.Space => ' ',
            ThousandsSeparatorKind.Apostrophe => '\'',
            _ => null,
        };
        if (_thousands == _decimal) throw new ArgumentException("Decimal and thousands separators must differ.", nameof(options));
    }

    /// <summary>Finds all number tokens. Returns false with a reason when a malformed number is present.</summary>
    public bool TryScan(string text, out List<NumberToken> tokens, out string? error)
    {
        tokens = [];
        error = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var signStart = -1;
            var j = i;
            if (c is '+' or '-')
            {
                // Sign followed by optional spaces and a digit.
                var k = i + 1;
                while (k < text.Length && text[k] == ' ') k++;
                if (k < text.Length && char.IsAsciiDigit(text[k]))
                {
                    signStart = i;
                    j = k;
                }
                else
                {
                    i++;
                    continue;
                }
            }
            else if (c == _decimal && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) && (i == 0 || !IsWordChar(text[i - 1])))
            {
                // ".5" style value without a leading zero.
                j = i;
            }
            else if (char.IsAsciiDigit(c) && i > 0 && char.IsAsciiLetter(text[i - 1]) && PrecededByUnit(text, i))
            {
                // "12500kg12510kg": digits right after a unit symbol start a new number (two frames glued
                // together). Treating them as an identifier would silently hide the second value.
                j = i;
            }
            else if (!char.IsAsciiDigit(c) || (i > 0 && IsWordChar(text[i - 1])))
            {
                if (char.IsAsciiDigit(c))
                {
                    // Digits glued to letters ("W1", "A9") belong to an identifier, not to the weight.
                    while (i < text.Length && (char.IsAsciiDigit(text[i]) || IsWordChar(text[i]))) i++;
                    continue;
                }
                i++;
                continue;
            }

            if (!TryReadNumber(text, j, out var end, out var digits, out var decimals, out var normalized, out error))
            {
                return false;
            }

            // Reversed-digit indicators may put the sign after the number.
            var tokenStart = signStart >= 0 ? signStart : j;
            var negative = signStart >= 0 && text[signStart] == '-';
            var explicitSign = signStart >= 0;
            if (_options.ReverseDigits && end < text.Length && text[end] is '-' or '+' && signStart < 0)
            {
                negative = text[end] == '-';
                explicitSign = true;
                end++;
            }

            var raw = text[tokenStart..end];
            if (_options.ReverseDigits)
            {
                if (!TryReverse(raw, out normalized, out digits, out decimals, out negative, out error)) return false;
            }

            if (digits > _options.MaxDigits)
            {
                error = $"Number '{raw}' has {digits} digits (maximum {_options.MaxDigits}).";
                return false;
            }

            var value = decimal.Parse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            if (negative) value = -value;
            tokens.Add(new NumberToken(tokenStart, end - tokenStart, raw, value, decimals, explicitSign, digits));
            i = end;
        }
        return true;
    }

    /// <summary>Parses a field that must contain exactly one number and nothing else except spaces and a unit/status suffix.</summary>
    public bool TryParseSingle(string field, out NumberToken? token, out string? error, out string suffix)
    {
        token = null;
        suffix = string.Empty;
        if (!TryScan(field, out var tokens, out error)) return false;
        if (tokens.Count != 1)
        {
            error = tokens.Count == 0 ? $"No number in '{field}'." : $"Field '{field}' contains {tokens.Count} numbers.";
            return false;
        }
        var t = tokens[0];
        var prefix = field[..t.Start].Trim();
        if (prefix.Length > 0 && prefix != "=" && prefix != ":")
        {
            error = $"Unexpected text '{prefix}' before the number in '{field}'.";
            return false;
        }
        suffix = field[t.End..].Trim();
        token = t;
        return true;
    }

    private bool TryReadNumber(string text, int start, out int end, out int digits, out int decimals, out string normalized, out string? error)
    {
        var sb = new StringBuilder();
        digits = 0;
        decimals = 0;
        error = null;
        var seenDecimal = false;
        var groupDigits = 0;
        var usedGrouping = false;
        var firstGroupDigits = 0;
        var k = start;
        while (k < text.Length)
        {
            var ch = text[k];
            if (char.IsAsciiDigit(ch))
            {
                sb.Append(ch);
                digits++;
                if (seenDecimal) decimals++;
                else groupDigits++;
                k++;
                continue;
            }

            if (ch == _decimal && !seenDecimal && k + 1 < text.Length && char.IsAsciiDigit(text[k + 1]))
            {
                if (usedGrouping && groupDigits != 3)
                {
                    error = $"Malformed thousands grouping in '{Snippet(text, start, k + 1)}'.";
                    end = k;
                    normalized = string.Empty;
                    return false;
                }
                seenDecimal = true;
                sb.Append('.');
                k++;
                continue;
            }

            if (ch == _decimal && seenDecimal && k + 1 < text.Length && char.IsAsciiDigit(text[k + 1]))
            {
                error = $"Number '{Snippet(text, start, k + 2)}' has more than one decimal separator.";
                end = k;
                normalized = string.Empty;
                return false;
            }

            if (_thousands is { } sep && ch == sep && !seenDecimal && k + 3 < text.Length + 1
                && CountDigits(text, k + 1) == 3)
            {
                if (!usedGrouping)
                {
                    firstGroupDigits = groupDigits;
                    if (firstGroupDigits is < 1 or > 3)
                    {
                        error = $"Malformed thousands grouping in '{Snippet(text, start, k + 4)}'.";
                        end = k;
                        normalized = string.Empty;
                        return false;
                    }
                }
                else if (groupDigits != 3)
                {
                    error = $"Malformed thousands grouping in '{Snippet(text, start, k + 4)}'.";
                    end = k;
                    normalized = string.Empty;
                    return false;
                }
                usedGrouping = true;
                groupDigits = 0;
                k++;
                continue;
            }

            break;
        }

        if (usedGrouping && !seenDecimal && groupDigits != 3)
        {
            error = $"Malformed thousands grouping in '{Snippet(text, start, k)}'.";
            end = k;
            normalized = string.Empty;
            return false;
        }

        end = k;
        normalized = sb.ToString();
        return true;
    }

    private static bool TryReverse(string raw, out string normalized, out int digits, out int decimals, out bool negative, out string? error)
    {
        negative = raw.Contains('-');
        var chars = raw.Where(ch => ch != '+' && ch != '-' && ch != ' ').Reverse().ToArray();
        var reversed = new string(chars);
        digits = chars.Count(char.IsAsciiDigit);
        var dot = reversed.IndexOfAny(['.', ',']);
        if (dot >= 0 && reversed.IndexOfAny(['.', ','], dot + 1) >= 0)
        {
            error = $"Reversed number '{raw}' has more than one separator.";
            normalized = string.Empty;
            decimals = 0;
            return false;
        }
        decimals = dot >= 0 ? reversed.Length - dot - 1 : 0;
        normalized = dot >= 0 ? reversed[..dot].PadLeft(1, '0') + "." + reversed[(dot + 1)..] : reversed;
        if (normalized.EndsWith('.')) normalized += "0";
        error = null;
        return true;
    }

    private static bool PrecededByUnit(string text, int digitIndex)
    {
        var start = digitIndex;
        while (start > 0 && char.IsAsciiLetter(text[start - 1])) start--;
        // The letters must themselves follow a non-word character or a digit ("…5kg12…"), not be part of a word.
        if (start > 0 && text[start - 1] == '_') return false;
        return Units.WeightUnits.TryParse(text[start..digitIndex], out _);
    }

    private static int CountDigits(string text, int from)
    {
        var n = 0;
        while (from + n < text.Length && char.IsAsciiDigit(text[from + n])) n++;
        return n;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || c == '_';

    private static string Snippet(string text, int start, int end) => text[start..Math.Min(text.Length, Math.Max(start, end))];
}
