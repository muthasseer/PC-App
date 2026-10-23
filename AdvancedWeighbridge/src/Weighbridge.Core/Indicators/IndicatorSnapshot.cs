using Weighbridge.Core.Indicators.Stability;

namespace Weighbridge.Core.Indicators;

public enum ConnectionState
{
    Disabled = 0,
    Disconnected = 1,
    Connecting = 2,
    Connected = 3,

    /// <summary>Port open but no data within the read timeout (indicator off, cable, wrong pin-out, needs polling).</summary>
    Silent = 4,

    Error = 5,
}

/// <summary>
/// Immutable view of an indicator at one instant. Published by the indicator service after every frame and read
/// lock-free by the UI, the automation engine and GET WEIGHT.
/// </summary>
public sealed record IndicatorSnapshot
{
    public ConnectionState Connection { get; init; } = ConnectionState.Disconnected;

    /// <summary>Latest successfully parsed reading (may carry overload/error flags).</summary>
    public WeightReading? Latest { get; init; }

    public StabilityState Stability { get; init; } = StabilityState.Unknown;

    /// <summary>Arrival time of the newest frame of any kind (parsed, ignored or rejected).</summary>
    public DateTimeOffset? LastFrameAt { get; init; }

    public string? LastRejectReason { get; init; }
    public DateTimeOffset? LastRejectAt { get; init; }

    public long FramesParsed { get; init; }
    public long FramesRejected { get; init; }
    public long ReconnectCount { get; init; }

    public string? LastError { get; init; }

    public Guid? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public int? ProfileVersion { get; init; }
    public string? PortDescription { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public static IndicatorSnapshot Disconnected(DateTimeOffset at) => new() { Connection = ConnectionState.Disconnected, Timestamp = at };

    public double? AgeMs(DateTimeOffset now) => Latest is null ? null : (now - Latest.ReceivedAt).TotalMilliseconds;

    /// <summary>The newest frame received was rejected (so the latest valid reading may no longer be current).</summary>
    public bool NewestFrameInvalid => LastRejectAt is { } r && (Latest is null || r > Latest.ReceivedAt);
}
