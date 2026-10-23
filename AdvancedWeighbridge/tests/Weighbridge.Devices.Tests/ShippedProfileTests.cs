using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Tests;

/// <summary>Every example profile shipped with the installer must be valid and decode its sample capture.</summary>
public class ShippedProfileTests
{
    private static string RepoFolder(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AdvancedWeighbridge.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), name);
    }

    public static IEnumerable<object[]> Profiles() =>
        Directory.GetFiles(RepoFolder("profiles"), "*.wbprofile.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Example_profile_decodes_its_sample_capture(string file)
    {
        var profile = IndicatorProfile.FromJson(File.ReadAllText(Path.Combine(RepoFolder("profiles"), file)));
        Assert.Empty(profile.Validate(ProtocolRegistry.CreateDefault()));
        Assert.False(profile.Verified); // examples must be verified on site
        Assert.Contains("EXAMPLE", profile.Notes);

        var capture = Path.Combine(RepoFolder("samples"), file.Replace(".wbprofile.json", ".wbcap", StringComparison.Ordinal));
        var (_, records) = RawCaptureFormat.Load(capture);
        var decoder = new IndicatorDecoder(profile);
        var start = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var results = records.SelectMany(r => decoder.Feed(r.Data, start.AddMilliseconds(r.OffsetMs))).ToList();
        Assert.True(results.Count >= 200, $"only {results.Count} frames");
        var parsed = results.Where(r => r.Result.Outcome == ParseOutcome.Parsed).ToList();
        Assert.True(parsed.Count >= results.Count * 0.98, $"{file}: parsed {parsed.Count} of {results.Count}; first problem: {results.FirstOrDefault(r => r.Result.Outcome != ParseOutcome.Parsed)?.Result.Reason}");
        var max = parsed.Max(r => r.Result.Reading!.Weight!.Value.Kilograms);
        Assert.InRange(max, 30_000m, 34_000m); // the sample truck: 32 500 kg gross
        Assert.All(parsed, r => Assert.InRange(r.Result.Reading!.Weight!.Value.Kilograms, -100m, 34_000m));
    }
}
