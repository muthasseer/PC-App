using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Weighbridge.Core.Weighing;

public enum SequenceReset
{
    Never = 0,
    Yearly = 1,
    Monthly = 2,
    Daily = 3,
}

public sealed record TicketNumberOptions
{
    /// <summary>Tokens: {SITE} {WB} {YYYY} {YY} {MM} {DD} {SEQ} {SEQ:n} (n = zero-padded width).</summary>
    public string Pattern { get; init; } = "{SITE}-{YYYY}-{SEQ:6}";

    public SequenceReset Reset { get; init; } = SequenceReset.Yearly;
}

public static partial class TicketNumbering
{
    [GeneratedRegex(@"\{(SITE|WB|YYYY|YY|MM|DD|SEQ)(?::(\d{1,2}))?\}")]
    private static partial Regex TokenRegex();

    /// <summary>Key of the counter the sequence number is drawn from (counters restart when the key changes).</summary>
    public static string ScopeKey(TicketNumberOptions options, string siteCode, string weighbridgeCode, DateTime localDate)
    {
        var period = options.Reset switch
        {
            SequenceReset.Yearly => localDate.ToString("yyyy", CultureInfo.InvariantCulture),
            SequenceReset.Monthly => localDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            SequenceReset.Daily => localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => "all",
        };
        // Only include the weighbridge in the key when the pattern distinguishes weighbridges.
        var wb = options.Pattern.Contains("{WB", StringComparison.Ordinal) ? weighbridgeCode : "*";
        return $"ticket|{siteCode}|{wb}|{period}";
    }

    public static string Format(TicketNumberOptions options, string siteCode, string weighbridgeCode, DateTime localDate, long sequence)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        return TokenRegex().Replace(options.Pattern, m =>
        {
            var width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return m.Groups[1].Value switch
            {
                "SITE" => Sanitize(siteCode),
                "WB" => Sanitize(weighbridgeCode),
                "YYYY" => localDate.ToString("yyyy", CultureInfo.InvariantCulture),
                "YY" => localDate.ToString("yy", CultureInfo.InvariantCulture),
                "MM" => localDate.ToString("MM", CultureInfo.InvariantCulture),
                "DD" => localDate.ToString("dd", CultureInfo.InvariantCulture),
                _ => sequence.ToString(width > 0 ? new string('0', width) : "0", CultureInfo.InvariantCulture),
            };
        });
    }

    public static IReadOnlyList<string> Validate(TicketNumberOptions options)
    {
        var problems = new List<string>();
        if (!options.Pattern.Contains("{SEQ", StringComparison.Ordinal)) problems.Add("The ticket number pattern must contain {SEQ} so that numbers are unique.");
        var periodTokens = options.Reset switch
        {
            SequenceReset.Daily => new[] { "{YYYY}", "{YY}" }.Any(options.Pattern.Contains) && options.Pattern.Contains("{MM}") && options.Pattern.Contains("{DD}"),
            SequenceReset.Monthly => new[] { "{YYYY}", "{YY}" }.Any(options.Pattern.Contains) && options.Pattern.Contains("{MM}"),
            SequenceReset.Yearly => new[] { "{YYYY}", "{YY}" }.Any(options.Pattern.Contains),
            _ => true,
        };
        if (!periodTokens) problems.Add("The pattern must contain the date parts of the reset period, otherwise ticket numbers would repeat.");
        return problems;
    }

    private static string Sanitize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var ch in code.Trim().ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
        }
        return sb.Length == 0 ? "X" : sb.ToString();
    }
}

public static class VehicleRegistration
{
    /// <summary>Comparison key: upper case, letters and digits only ("WP CAB-1234" → "WPCAB1234").</summary>
    public static string Normalize(string? registration)
    {
        if (string.IsNullOrWhiteSpace(registration)) return string.Empty;
        var sb = new StringBuilder(registration.Length);
        foreach (var ch in registration.Normalize(NormalizationForm.FormC))
        {
            // Keep vowel signs / virama (Sinhala, Tamil) so that different prefixes do not collide; drop
            // spaces, punctuation and zero-width joiners.
            var category = char.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }
        return sb.ToString();
    }

    /// <summary>Tidy display form: trimmed, single spaces, upper case.</summary>
    public static string Display(string? registration)
    {
        if (string.IsNullOrWhiteSpace(registration)) return string.Empty;
        var parts = registration.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
