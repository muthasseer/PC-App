using System.Text;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Learning;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Tests;

public class LearningTests
{
    private static byte[] Stream(params string[] frames) => Encoding.Latin1.GetBytes(string.Concat(frames));

    [Fact]
    public void Detects_crlf_terminated_stream()
    {
        var data = Stream(Enumerable.Range(0, 20).Select(i => $"ST,GS,+{12500 + i * 10:000000.0}kg\r\n").ToArray());
        var best = FramingAnalyzer.Analyze(data)[0];
        Assert.Equal(FramingMode.Terminator, best.Framing.Mode);
        Assert.Equal("\\r\\n", best.Framing.Terminator);
        Assert.True(best.Confidence > 0.8);
    }

    [Fact]
    public void Detects_stx_cr_frames_with_checksum_trailer()
    {
        var frames = Enumerable.Range(0, 10).SelectMany(i => ToledoContinuousProtocol.Encode(12000 + i, false, withChecksum: true)).ToArray();
        var best = FramingAnalyzer.Analyze(frames)[0];
        Assert.Equal(FramingMode.StartEnd, best.Framing.Mode);
        Assert.Equal("<STX>", best.Framing.StartMarker);
        Assert.Equal("<CR>", best.Framing.EndMarker);
        Assert.Equal(1, best.Framing.TrailerLength);
    }

    [Fact]
    public void Detects_fixed_length_without_delimiters()
    {
        var data = Stream(Enumerable.Range(0, 30).Select(i => $"={(12500 + i):0000000}").ToArray());
        var suggestions = FramingAnalyzer.Analyze(data);
        var fixedLength = suggestions.First(s => s.Framing.Mode == FramingMode.FixedLength);
        Assert.Equal(8, fixedLength.Framing.FixedLength);
        Assert.Equal("=", fixedLength.Framing.StartMarker);
    }

    [Fact]
    public void Falls_back_to_idle_gap_for_unstructured_data()
    {
        var best = FramingAnalyzer.Analyze(Stream("12500 12500 12510 12520"))[0];
        Assert.Equal(FramingMode.IdleGap, best.Framing.Mode);
    }

    [Fact]
    public void Learns_status_unit_and_weight_from_samples()
    {
        var frames = new List<string>();
        for (var i = 0; i < 15; i++) frames.Add($"{(i < 10 ? "US" : "ST")},GS,+{8000 + i * 300:000000.0}kg");
        var analysis = FormatLearner.Analyze(frames);

        Assert.Equal(WeightUnit.Kilogram, analysis.DetectedUnit);
        Assert.Equal(1.0, analysis.ShapeCoverage);
        Assert.Single(analysis.WeightCandidates);
        Assert.True(analysis.SuggestedParseRate >= 0.99, string.Join("; ", analysis.Notes));

        var parser = new WeightParser(analysis.SuggestedParser);
        var r = parser.Parse(RawFrame.FromText("ST,GS,+012500.0kg", DateTimeOffset.UtcNow)).Reading!;
        Assert.Equal(12500m, r.Value);
        Assert.True(r.IndicatorStable);
    }

    [Fact]
    public void Ranks_weight_above_counter_and_station_number()
    {
        var frames = Enumerable.Range(0, 12).Select(i => $"{i + 1:0000} 01 {12000 + i * 50,7} kg").ToList();
        var analysis = FormatLearner.Analyze(frames);
        var top = analysis.WeightCandidates[0];
        Assert.Contains("kg", top.FollowingUnit);
        Assert.Equal(12550m, top.LatestValue);
        Assert.Equal(ParserMode.Regex, analysis.SuggestedParser.Mode);
        Assert.True(analysis.SuggestedParseRate >= 0.99);

        var r = new WeightParser(analysis.SuggestedParser).Parse(RawFrame.FromText("0013 01   12600 kg", DateTimeOffset.UtcNow));
        Assert.Equal(12600m, r.Reading!.Value);
        Assert.Contains(analysis.Notes, n => n.Contains("stable/motion"));
    }

    [Fact]
    public void Reports_unknown_status_codes()
    {
        var frames = Enumerable.Range(0, 8).Select(i => $"{(i % 2 == 0 ? "QX" : "QZ")} {1000 + i}").ToList();
        var analysis = FormatLearner.Analyze(frames);
        Assert.Contains(analysis.Notes, n => n.Contains("'QX'") && n.Contains("'QZ'"));
    }

    [Fact]
    public void Reference_weight_selects_implied_decimals()
    {
        // Indicator sends "W 0125005" while its display shows 12500.5 kg.
        var frames = Enumerable.Range(0, 6).Select(i => $"W {125000 + i:0000000}").ToList();
        var analysis = FormatLearner.Analyze(frames);
        var matches = FormatLearner.MatchReference(analysis, "W 0125005", 12500.5m, WeightUnit.Kilogram);
        Assert.NotEmpty(matches);
        var chosen = matches[0].Parser;
        Assert.Equal(1, chosen.ImpliedDecimals);
        Assert.Equal(12500.5m, new WeightParser(chosen).Parse(RawFrame.FromText("W 0125005", DateTimeOffset.UtcNow)).Reading!.Value);
    }

    [Fact]
    public void Reference_weight_selects_reversed_digits()
    {
        var frames = new[] { "=00.0210", "=00.0210", "=00.0310" };
        var analysis = FormatLearner.Analyze(frames);
        var matches = FormatLearner.MatchReference(analysis, "=00.0310", 130.00m, WeightUnit.Kilogram);
        Assert.Contains(matches, m => m.Parser.ReverseDigits);
    }

    [Fact]
    public void Reference_weight_in_tonnes()
    {
        var frames = Enumerable.Range(0, 5).Select(i => $"NT {25.40 + i * 0.02:00.00}").ToList();
        var analysis = FormatLearner.Analyze(frames);
        var matches = FormatLearner.MatchReference(analysis, "NT 25.48", 25.48m, WeightUnit.Tonne);
        Assert.NotEmpty(matches);
        var reading = new WeightParser(matches[0].Parser).Parse(RawFrame.FromText("NT 25.48", DateTimeOffset.UtcNow)).Reading!;
        Assert.Equal(25480m, reading.Weight!.Value.Kilograms);
    }

    [Fact]
    public void Line_quality_prefers_clean_ascii()
    {
        var good = LineQualityScorer.Score(new(9600, 8, ParityKind.None, StopBitsKind.One), Stream("ST,GS,+012500.0kg\r\nST,GS,+012500.0kg\r\n"));
        var noise = LineQualityScorer.Score(new(2400, 8, ParityKind.None, StopBitsKind.One), [0x80, 0xF3, 0x1F, 0xE0, 0x00, 0x9C, 0xFF, 0xA5, 0x13, 0xC8]);
        Assert.True(good.LooksValid);
        Assert.False(noise.LooksValid);
        Assert.True(good.Score > noise.Score);
        Assert.Equal(0, LineQualityScorer.Score(good.Settings, []).Score);
        Assert.Equal(24, LineQualityScorer.DefaultScanOrder().Count());
    }
}
