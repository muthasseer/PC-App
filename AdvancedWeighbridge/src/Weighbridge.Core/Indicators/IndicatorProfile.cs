using System.Text.Json;
using System.Text.Json.Serialization;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Stability;

namespace Weighbridge.Core.Indicators;

public enum TransportKind
{
    Serial = 0,

    /// <summary>Serial-to-Ethernet converter or indicator with a TCP port (raw socket client).</summary>
    TcpClient = 1,

    /// <summary>Built-in simulator for training, development and acceptance tests without hardware.</summary>
    Simulator = 2,

    /// <summary>Replays a saved raw capture file.</summary>
    Replay = 3,
}

public enum ParityKind { None = 0, Odd = 1, Even = 2, Mark = 3, Space = 4 }

public enum StopBitsKind { One = 1, Two = 2, OnePointFive = 3 }

public enum HandshakeKind { None = 0, XOnXOff = 1, RequestToSend = 2, RequestToSendXOnXOff = 3 }

public sealed record SerialSettings
{
    public string PortName { get; init; } = "COM1";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public ParityKind Parity { get; init; } = ParityKind.None;
    public StopBitsKind StopBits { get; init; } = StopBitsKind.One;
    public HandshakeKind Handshake { get; init; } = HandshakeKind.None;
    public bool DtrEnable { get; init; } = true;
    public bool RtsEnable { get; init; } = true;
    public int ReadBufferSize { get; init; } = 8192;

    public string Summary => $"{PortName} {BaudRate} {DataBits}{Parity.ToString()[0]}{StopBits switch { StopBitsKind.One => "1", StopBitsKind.Two => "2", _ => "1.5" }}";
}

public sealed record TcpSettings
{
    public string Host { get; init; } = "192.168.1.100";
    public int Port { get; init; } = 4001;
}

public sealed record SimulatorSettings
{
    /// <summary>Output format the simulator emits, in profile notation with {weight}, {status}, {mode}, {unit} placeholders.</summary>
    public string Template { get; init; } = "{status},{mode},{weight:+000000.0}kg\\r\\n";
    public int IntervalMs { get; init; } = 100;
    public decimal NoiseKg { get; init; } = 5m;
    public decimal TruckGrossKg { get; init; } = 32500m;
    public decimal TruckTareKg { get; init; } = 12300m;

    /// <summary>When true the simulator drives a vehicle on and off the deck automatically.</summary>
    public bool AutoCycle { get; init; } = true;
}

public sealed record ConnectionSettings
{
    public TransportKind Transport { get; init; } = TransportKind.Serial;
    public SerialSettings Serial { get; init; } = new();
    public TcpSettings Tcp { get; init; } = new();
    public SimulatorSettings Simulator { get; init; } = new();
    public string? ReplayFile { get; init; }

    /// <summary>No data for this long → the indicator is reported as silent (stale).</summary>
    public int ReadTimeoutMs { get; init; } = 3000;

    public int ReconnectIntervalMs { get; init; } = 3000;
}

public sealed record PollSettings
{
    public bool Enabled { get; init; }

    /// <summary>Request sent to the indicator, in profile notation (e.g. <c>"R\r\n"</c>, <c>"&lt;ENQ&gt;"</c>, <c>"P"</c>).</summary>
    public string Command { get; init; } = "<ENQ>";

    public int IntervalMs { get; init; } = 250;
}

/// <summary>
/// Complete description of how to talk to one indicator model: connection, framing, checksum, parser and
/// stability rules. Saved as JSON (<c>*.wbprofile.json</c>), exportable and importable. Profiles are versioned;
/// every capture records the profile id and version that produced it.
/// </summary>
public sealed record IndicatorProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "New indicator";
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Notes { get; init; }

    public ConnectionSettings Connection { get; init; } = new();
    public FramingOptions Framing { get; init; } = new();
    public ChecksumOptions Checksum { get; init; } = new();
    public ParserOptions Parser { get; init; } = new();
    public StabilityOptions Stability { get; init; } = new();
    public PollSettings Poll { get; init; } = new();

    /// <summary>True when the profile was verified against a reference load (setup wizard step 10).</summary>
    public bool Verified { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public string? VerifiedBy { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static IndicatorProfile FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<IndicatorProfile>(json, JsonOptions)
                      ?? throw new FormatException("The profile file is empty.");
        if (profile.SchemaVersion > CurrentSchemaVersion)
        {
            throw new FormatException($"The profile was created by a newer version of the software (schema {profile.SchemaVersion}).");
        }
        return profile;
    }

    /// <summary>Validates the profile by constructing its framing and parser. Returns the problems found.</summary>
    public IReadOnlyList<string> Validate(Protocols.ProtocolRegistry? protocols = null)
    {
        var problems = new List<string>();
        try { _ = new FrameAssembler(Framing); }
        catch (ArgumentException e) { problems.Add("Framing: " + e.Message); }
        try { _ = new WeightParser(Parser, protocols); }
        catch (ArgumentException e) { problems.Add("Parser: " + e.Message); } // includes RegexParseException
        if (Connection.Transport == TransportKind.Serial)
        {
            var s = Connection.Serial;
            if (string.IsNullOrWhiteSpace(s.PortName)) problems.Add("Serial: no COM port selected.");
            if (s.BaudRate is < 50 or > 4_000_000) problems.Add($"Serial: baud rate {s.BaudRate} is out of range.");
            if (s.DataBits is < 5 or > 8) problems.Add($"Serial: data bits must be 5–8 (was {s.DataBits}).");
            if (s.StopBits == StopBitsKind.OnePointFive && s.DataBits != 5) problems.Add("Serial: 1.5 stop bits are only valid with 5 data bits on most UARTs.");
        }
        if (Connection.Transport == TransportKind.TcpClient && (Connection.Tcp.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Connection.Tcp.Host)))
        {
            problems.Add("TCP: host and port are required.");
        }
        if (Stability.ToleranceKg < 0 || Stability.WindowMs < 0) problems.Add("Stability: tolerance and window must be positive.");
        if (Poll.Enabled && Poll.IntervalMs < 20) problems.Add("Poll: interval must be at least 20 ms.");
        return problems;
    }

    public IndicatorProfile NextVersion() => this with { Version = Version + 1, Verified = false, VerifiedAt = null, VerifiedBy = null };
}
