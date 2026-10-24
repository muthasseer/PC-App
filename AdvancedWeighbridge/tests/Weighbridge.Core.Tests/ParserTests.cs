using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Tests;

public class ParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static ParseResult Parse(string text, ParserOptions? options = null) =>
        new WeightParser(options ?? new ParserOptions()).Parse(RawFrame.FromText(text, T0));

    private static WeightReading Ok(string text, ParserOptions? options = null)
    {
        var r = Parse(text, options);
        Assert.True(r.Outcome == ParseOutcome.Parsed, $"'{text}' was {r.Outcome}: {r.Reason}");
        return r.Reading!;
    }

    private static string Rejected(string text, ParserOptions? options = null)
    {
        var r = Parse(text, options);
        Assert.True(r.Outcome == ParseOutcome.Rejected, $"'{text}' should be rejected but was {r.Outcome} ({r.Reading?.Value})");
        return r.Reason!;
    }

    // ---- The example formats from the specification -----------------------------------------------

    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("12345 kg", 12345)]
    [InlineData("+000123.45", 123.45)]
    [InlineData("Gross: 12500 kg", 12500)]
    [InlineData("WT=12500.0", 12500.0)]
    [InlineData("   -  1234 kg  ", -1234)]
    [InlineData("12345kgs", 12345)]
    [InlineData(".5", 0.5)]
    public void Auto_mode_parses_common_formats(string text, double expectedKg)
    {
        var r = Ok(text);
        Assert.Equal((decimal)expectedKg, r.Weight!.Value.Kilograms);
    }

    [Fact]
    public void Status_gross_stable_with_unit()
    {
        var r = Ok("ST,GS,+001234.5kg");
        Assert.Equal(1234.5m, r.Value);
        Assert.Equal(WeightUnit.Kilogram, r.Unit);
        Assert.True(r.IndicatorStable);
        Assert.True(r.Flags.HasFlag(ReadingFlags.Gross));
        Assert.Equal(1, r.ReportedDecimals);
    }

    [Fact]
    public void Unstable_net_in_tonnes_by_profile_default_unit()
    {
        var r = Ok("US,NT,00025.40", new ParserOptions { DefaultUnit = WeightUnit.Tonne });
        Assert.Equal(25.40m, r.Value);
        Assert.Equal(25400m, r.Weight!.Value.Kilograms);
        Assert.False(r.IndicatorStable);
        Assert.True(r.Flags.HasFlag(ReadingFlags.Net));
    }

    [Fact]
    public void Gross_word_sets_gross_flag()
    {
        Assert.True(Ok("Gross: 12500 kg").Flags.HasFlag(ReadingFlags.Gross));
    }

    // ---- Rejections: never extract a wrong number ------------------------------------------------

    [Theory]
    [InlineData("12500kg12510kg")]          // two frames glued together
    [InlineData("12,500")]                  // comma is not configured as thousands separator
    [InlineData("2026-01-05 10:22 12500kg")] // date/time plus weight
    [InlineData("T1 12500kg")]               // a unit letter glued to digits is not trusted as a label
    public void Ambiguous_frames_are_rejected(string text)
    {
        Assert.Contains("mbiguous", Rejected(text));
    }

    [Theory]
    [InlineData("12.34.5", "decimal separator")]
    [InlineData("ST,GS,+0012\u0001", "control")]
    [InlineData("1234567890123", "digits")]
    [InlineData("99999999 kg", "Implausible")]
    [InlineData("ST,GS", "No weight")]
    [InlineData("12345 t", "single letter")]
    [InlineData("12345 kg 12 lb", "mbiguous")]
    [InlineData("kg 12500 lb", "Conflicting units")]
    public void Malformed_frames_are_rejected_with_reason(string text, string reasonFragment)
    {
        Assert.Contains(reasonFragment, Rejected(text), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_bytes_are_rejected_in_ascii_mode()
    {
        var frame = new RawFrame([0x31, 0x32, 0xB3, 0x34], [0x31, 0x32, 0xB3, 0x34], [], T0, 1);
        Assert.Equal(ParseOutcome.Rejected, new WeightParser(new ParserOptions()).Parse(frame).Outcome);
    }

    [Fact]
    public void Negative_rejected_when_profile_disallows()
    {
        Assert.Contains("Negative", Rejected("-1234", new ParserOptions { AllowNegative = false }));
    }

    [Fact]
    public void Empty_frame_is_ignored_not_rejected()
    {
        Assert.Equal(ParseOutcome.Ignored, Parse("   ").Outcome);
        Assert.Equal(ParseOutcome.Ignored, Parse("WEIGHBRIDGE 1", new ParserOptions { IgnorePattern = "^WEIGHBRIDGE" }).Outcome);
    }

    [Fact]
    public void Validation_pattern_is_enforced()
    {
        var o = new ParserOptions { ValidationPattern = @"^(ST|US),(GS|NT)," };
        Ok("ST,GS,+001234.5kg", o);
        Rejected("XX,GS,+001234.5kg", o);
    }

    // ---- Faults ------------------------------------------------------------------------------

    [Theory]
    [InlineData("OL")]
    [InlineData("-OL-")]
    [InlineData("ST,GS,OVERLOAD")]
    public void Overload_is_parsed_as_unusable_reading(string text)
    {
        var r = Ok(text);
        Assert.True(r.IsOverload);
        Assert.False(r.IsUsable);
        Assert.Null(r.Value);
    }

    [Fact]
    public void Error_code_with_number_is_not_usable()
    {
        var r = Ok("ERR 05");
        Assert.True(r.IsError);
        Assert.False(r.IsUsable);
    }

    // ---- Number format options ---------------------------------------------------------------------

    [Fact]
    public void Single_letter_unit_when_enabled()
    {
        var r = Ok("25.40 t", new ParserOptions { AllowSingleLetterUnits = true });
        Assert.Equal(25400m, r.Weight!.Value.Kilograms);
    }

    [Fact]
    public void Single_letter_status_rule_takes_precedence_over_unit_letter()
    {
        var o = new ParserOptions { StatusRules = [new StatusRule("G", ReadingFlags.Gross, StatusMatch.Word, CaseSensitive: true)] };
        var r = Ok("+0012500G", o);
        Assert.Equal(12500m, r.Value);
        Assert.True(r.Flags.HasFlag(ReadingFlags.Gross));
    }

    [Theory]
    [InlineData("12,500 kg", ThousandsSeparatorKind.Comma, DecimalSeparatorKind.Point, 12500)]
    [InlineData("123,456.5", ThousandsSeparatorKind.Comma, DecimalSeparatorKind.Point, 123456.5)]
    [InlineData("12.500,5", ThousandsSeparatorKind.Point, DecimalSeparatorKind.Comma, 12500.5)]
    [InlineData("12 500", ThousandsSeparatorKind.Space, DecimalSeparatorKind.Point, 12500)]
    [InlineData("12'500", ThousandsSeparatorKind.Apostrophe, DecimalSeparatorKind.Point, 12500)]
    [InlineData("1234,5", ThousandsSeparatorKind.None, DecimalSeparatorKind.Comma, 1234.5)]
    public void Thousands_and_decimal_separators(string text, ThousandsSeparatorKind thousands, DecimalSeparatorKind dec, double expected)
    {
        var r = Ok(text, new ParserOptions { ThousandsSeparator = thousands, DecimalSeparator = dec });
        Assert.Equal((decimal)expected, r.Value);
    }

    [Theory]
    [InlineData("1,23,456")]
    [InlineData("12,50")]
    public void Broken_thousands_grouping_is_rejected(string text)
    {
        Rejected(text, new ParserOptions { ThousandsSeparator = ThousandsSeparatorKind.Comma });
    }

    [Fact]
    public void Implied_decimals_and_multiplier()
    {
        Assert.Equal(1234.5m, Ok("012345", new ParserOptions { ImpliedDecimals = 1 }).Value);
        Assert.Equal(12340m, Ok("1234", new ParserOptions { Multiplier = 10 }).Value);
        // An explicit decimal point wins over implied decimals.
        Assert.Equal(1234.5m, Ok("1234.5", new ParserOptions { ImpliedDecimals = 2 }).Value);
    }

    [Theory]
    [InlineData("=00.0210", 120.00)]
    [InlineData("=00.0210-", -120.00)]
    [InlineData("=0052100", 12500)]
    public void Reversed_digit_indicators(string text, double expected)
    {
        Assert.Equal((decimal)expected, Ok(text, new ParserOptions { ReverseDigits = true }).Value);
    }

    // ---- Regex, delimited, fixed-position ---------------------------------------------------------

    [Fact]
    public void Regex_mode_with_named_groups()
    {
        var o = new ParserOptions
        {
            Mode = ParserMode.Regex,
            Pattern = @"^(?<status>ST|US),(?<mode>GS|NT),(?<weight>[+-]?\d+(\.\d+)?)(?<unit>kg|t)$",
        };
        var r = Ok("US,NT,-000250.0kg", o);
        Assert.Equal(-250.0m, r.Value);
        Assert.False(r.IndicatorStable);
        Assert.True(r.Flags.HasFlag(ReadingFlags.Net));
        Rejected("US;NT;-000250.0kg", o);
    }

    [Fact]
    public void Regex_mode_flag_groups_and_sign_group()
    {
        var o = new ParserOptions { Mode = ParserMode.Regex, Pattern = @"^W(?<sign>[ -])(?<weight>\d{6})(?<stable>S)?$" };
        var stable = Ok("W-012500S", o);
        Assert.Equal(-12500m, stable.Value);
        Assert.True(stable.IndicatorStable);
        Assert.Null(Ok("W 012500", o).IndicatorStable);
    }

    [Fact]
    public void Regex_pattern_without_weight_group_is_a_configuration_error()
    {
        Assert.Throws<ArgumentException>(() => new WeightParser(new ParserOptions { Mode = ParserMode.Regex, Pattern = @"\d+" }));
    }

    [Fact]
    public void Delimited_mode_auto_weight_field()
    {
        var r = Ok("ST,GS,+001234.5kg", new ParserOptions { Mode = ParserMode.Delimited });
        Assert.Equal(1234.5m, r.Value);
        Assert.True(r.IndicatorStable);
        Assert.True(r.Flags.HasFlag(ReadingFlags.Gross));
    }

    [Fact]
    public void Delimited_mode_explicit_fields_and_ambiguity()
    {
        var o = new ParserOptions { Mode = ParserMode.Delimited, Delimiter = ";", WeightField = 2, UnitField = 3 };
        var r = Ok("01;ST;12500;kg", o);
        Assert.Equal(12500m, r.Value);
        Assert.True(r.IndicatorStable);

        // Without the explicit weight field, "01" and "12500" are both numeric → ambiguous.
        Assert.Contains("mbiguous", Rejected("01;ST;12500;kg", o with { WeightField = -1 }));
        Assert.Contains("missing", Rejected("01;ST", o));
    }

    [Fact]
    public void Fixed_position_mode()
    {
        var o = new ParserOptions
        {
            Mode = ParserMode.FixedPosition,
            StatusSpans = [new FieldSpan(0, 2)],
            SignSpan = new FieldSpan(2, 1),
            WeightSpan = new FieldSpan(3, 6),
            UnitSpan = new FieldSpan(9, 2),
        };
        var r = Ok("ST-012500kg", o);
        Assert.Equal(-12500m, r.Value);
        Assert.True(r.IndicatorStable);
        Assert.Contains("needs", Rejected("ST-0125", o));
        Assert.Contains("sign", Rejected("ST*012500kg", o));
    }

    // ---- Built-in binary protocol ---------------------------------------------------------------

    [Fact]
    public void Toledo_continuous_round_trip_with_checksum()
    {
        var protocol = new ToledoContinuousProtocol();
        var profile = new IndicatorProfile
        {
            Framing = protocol.RecommendedFraming with { TrailerLength = 1 },
            Checksum = new ChecksumOptions { Kind = ChecksumKind.TwosComplement7 },
            Parser = new ParserOptions { Mode = ParserMode.Protocol, ProtocolName = protocol.Name },
        };
        var decoder = new IndicatorDecoder(profile);

        var stable = decoder.Feed(ToledoContinuousProtocol.Encode(12500m, motion: false, withChecksum: true), T0).Single();
        Assert.Equal(ParseOutcome.Parsed, stable.Result.Outcome);
        Assert.Equal(12500m, stable.Result.Reading!.Value);
        Assert.True(stable.Result.Reading.IndicatorStable);
        Assert.True(stable.Result.Reading.Flags.HasFlag(ReadingFlags.Gross));

        var tenths = decoder.Feed(ToledoContinuousProtocol.Encode(-1234.5m, motion: true, net: true, withChecksum: true, decimalCode: 3), T0).Single();
        Assert.Equal(-1234.5m, tenths.Result.Reading!.Value);
        Assert.False(tenths.Result.Reading.IndicatorStable);
        Assert.True(tenths.Result.Reading.Flags.HasFlag(ReadingFlags.Net));

        var corrupted = ToledoContinuousProtocol.Encode(12500m, motion: false, withChecksum: true);
        corrupted[6] = (byte)'9';
        var bad = decoder.Feed(corrupted, T0).Single();
        Assert.Equal(ParseOutcome.Rejected, bad.Result.Outcome);
        Assert.Contains("Checksum", bad.Result.Reason);
    }

    [Fact]
    public void Toledo_rejects_wrong_parity_status_bytes()
    {
        var frame = ToledoContinuousProtocol.Encode(100m, motion: false);
        frame[1] &= 0xDF; // clear bit 5 of SWA
        var profile = new IndicatorProfile
        {
            Framing = new ToledoContinuousProtocol().RecommendedFraming,
            Parser = new ParserOptions { Mode = ParserMode.Protocol, ProtocolName = "toledo-continuous" },
        };
        var result = new IndicatorDecoder(profile).Feed(frame, T0).Single().Result;
        Assert.Equal(ParseOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Toledo_overload_flag()
    {
        var profile = new IndicatorProfile
        {
            Framing = new ToledoContinuousProtocol().RecommendedFraming,
            Parser = new ParserOptions { Mode = ParserMode.Protocol, ProtocolName = "toledo-continuous" },
        };
        var r = new IndicatorDecoder(profile).Feed(ToledoContinuousProtocol.Encode(99999m, false, overload: true), T0).Single().Result.Reading!;
        Assert.True(r.IsOverload);
        Assert.False(r.IsUsable);
    }

    [Fact]
    public void Unknown_protocol_is_a_configuration_error()
    {
        Assert.Throws<ArgumentException>(() => new WeightParser(new ParserOptions { Mode = ParserMode.Protocol, ProtocolName = "nope" }));
    }

    // ---- Profiles ------------------------------------------------------------------------------

    [Fact]
    public void Profile_json_round_trip_and_validation()
    {
        var p = new IndicatorProfile
        {
            Name = "Test",
            Manufacturer = "Generic",
            Parser = new ParserOptions { Mode = ParserMode.Regex, Pattern = @"(?<weight>\d+)", StatusRules = [new StatusRule("S", ReadingFlags.Stable)] },
        };
        var json = p.ToJson();
        Assert.Contains("\"mode\": \"Regex\"", json);
        var back = IndicatorProfile.FromJson(json);
        Assert.Equal(p.Id, back.Id);
        Assert.Equal(p.Parser.Pattern, back.Parser.Pattern);
        Assert.Single(back.Parser.StatusRules);
        Assert.Empty(back.Validate());

        var broken = back with { Parser = back.Parser with { Pattern = "(" } };
        Assert.NotEmpty(broken.Validate());
    }

    [Fact]
    public void Decoder_end_to_end_from_bytes()
    {
        var decoder = new IndicatorDecoder(new IndicatorProfile());
        var frames = decoder.Feed(System.Text.Encoding.ASCII.GetBytes("ST,GS,+012500.0kg\r\nUS,GS,+012"), T0);
        Assert.Single(frames);
        Assert.Equal(12500m, frames[0].Result.Reading!.Value);
        frames = decoder.Feed(System.Text.Encoding.ASCII.GetBytes("510.0kg\r\n"), T0);
        Assert.Equal(12510m, frames.Single().Result.Reading!.Value);
    }
}
