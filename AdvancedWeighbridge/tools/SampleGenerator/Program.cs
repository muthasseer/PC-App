// Regenerates the example indicator profiles (profiles/) and their sample captures (samples/).
// Usage (from the AdvancedWeighbridge folder): dotnet run --project tools/SampleGenerator -- .

using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Units;
using Weighbridge.Devices.Simulation;
using Weighbridge.Devices.Transports;
using Microsoft.Extensions.Time.Testing;

var root = args[0];
var profiles = Path.Combine(root, "profiles");
var samples = Path.Combine(root, "samples");
Directory.CreateDirectory(profiles);
Directory.CreateDirectory(samples);
const string exampleNote = "EXAMPLE profile. Confirm the line settings and the decoded weight against the indicator display (set-up wizard step 4) before use.";

var list = new (string File, IndicatorProfile Profile, string Template)[]
{
    ("generic-status-mode-weight-kg", new IndicatorProfile
    {
        Id = Guid.Parse("3e1c6a10-0000-4a00-8000-000000000001"), Name = "Generic: ST/US,GS/NT,+000000.0kg", Manufacturer = "Generic", Model = "Status, mode, signed weight, unit", Notes = exampleNote,
        Parser = new ParserOptions { Mode = ParserMode.Auto }, Stability = new StabilityOptions { ToleranceKg = 20, WindowMs = 2000 },
    }, "{status},{mode},{weight:+000000.0}kg\\r\\n"),
    ("generic-plain-number", new IndicatorProfile
    {
        Id = Guid.Parse("3e1c6a10-0000-4a00-8000-000000000002"), Name = "Generic: plain number (kg)", Manufacturer = "Generic", Model = "Weight only, e.g. 12345 or 12345 kg", Notes = exampleNote + " The indicator does not report stability; the software decides from the readings.",
        Parser = new ParserOptions { Mode = ParserMode.Auto, DefaultUnit = WeightUnit.Kilogram }, Stability = new StabilityOptions { Mode = StabilityMode.Software, ToleranceKg = 20, WindowMs = 2500 },
    }, "{weight:0} kg\\r\\n"),
    ("generic-labelled-gross", new IndicatorProfile
    {
        Id = Guid.Parse("3e1c6a10-0000-4a00-8000-000000000003"), Name = "Generic: Gross: 12500 kg", Manufacturer = "Generic", Model = "Labelled gross weight", Notes = exampleNote,
        Parser = new ParserOptions { Mode = ParserMode.Regex, Pattern = @"^\s*Gross\s*:\s*(?<weight>[+-]?\d+(?:\.\d+)?)\s*(?<unit>kg|t)?\s*$" }, Stability = new StabilityOptions { Mode = StabilityMode.Software, ToleranceKg = 20 },
    }, "Gross: {weight:0} kg\\r\\n"),
    ("generic-wt-equals", new IndicatorProfile
    {
        Id = Guid.Parse("3e1c6a10-0000-4a00-8000-000000000004"), Name = "Generic: WT=12500.0", Manufacturer = "Generic", Model = "WT= prefix, kilograms", Notes = exampleNote,
        Parser = new ParserOptions { Mode = ParserMode.Regex, Pattern = @"^\s*WT\s*=\s*(?<weight>[+-]?\d+(?:\.\d+)?)\s*$" }, Stability = new StabilityOptions { Mode = StabilityMode.Software, ToleranceKg = 20 },
    }, "WT={weight:0.0}\\r\\n"),
    ("toledo-continuous", new IndicatorProfile
    {
        Id = Guid.Parse("3e1c6a10-0000-4a00-8000-000000000005"), Name = "Mettler Toledo continuous output", Manufacturer = "Mettler Toledo (and compatibles)", Model = "Continuous mode, STX ... CR + checksum",
        Notes = exampleNote + " Typical for IND-series and many compatible indicators. Status bytes give motion, overload and decimal position.",
        Connection = new ConnectionSettings { Serial = new SerialSettings { BaudRate = 9600, DataBits = 7, Parity = ParityKind.Even } },
        Framing = new ToledoContinuousProtocol().RecommendedFraming with { TrailerLength = 1 },
        Checksum = new ChecksumOptions { Kind = ChecksumKind.TwosComplement7 },
        Parser = new ParserOptions { Mode = ParserMode.Protocol, ProtocolName = "toledo-continuous" },
    }, "@toledo+cs"),
};
foreach (var (file, profile, template) in list)
{
    var problems = profile.Validate(ProtocolRegistry.CreateDefault());
    if (problems.Count > 0) throw new Exception($"{file}: {string.Join("; ", problems)}");
    File.WriteAllText(Path.Combine(profiles, file + ".wbprofile.json"), profile.ToJson());
    WriteCapture(Path.Combine(samples, file + ".wbcap"), template, file);
}
Console.WriteLine("done");

static void WriteCapture(string path, string template, string name)
{
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
    var world = new SimulatorWorld(new SimulatorSettings { NoiseKg = 4, DivisionKg = 10 }, time, seed: 7);
    using var w = new StreamWriter(path);
    w.WriteLine(System.Text.Json.JsonSerializer.Serialize(new RawCaptureFormat.Header("wbcap", 1, time.GetUtcNow(), "sample", $"Sample output for profile {name}: empty deck, loaded truck drives on, settles, drives off.")));
    long t = 0;
    void Emit(int ms)
    {
        for (var i = 0; i < ms / 100; i++)
        {
            var bytes = SimulatorWorld.Format(template, world.Read());
            w.WriteLine(System.Text.Json.JsonSerializer.Serialize(new RawCaptureFormat.Record(t, ByteText.ToHex(bytes, string.Empty))));
            time.Advance(TimeSpan.FromMilliseconds(100));
            t += 100;
        }
    }
    Emit(2000);
    world.DriveOn(world.Fleet[0], loaded: true);
    Emit(14000);
    world.DriveOff();
    Emit(6000);
}
