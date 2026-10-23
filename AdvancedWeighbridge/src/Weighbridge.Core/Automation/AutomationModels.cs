using Weighbridge.Core.Indicators;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Core.Automation;

public enum AutomationState
{
    /// <summary>Manual mode: the engine does not drive lights or barriers.</summary>
    Disabled,
    Idle,
    AwaitingIdentification,
    AwaitingPosition,
    AwaitingStable,
    Capturing,

    /// <summary>Captured; waiting for the operator to release the vehicle (semi-automatic, or auto-open disabled).</summary>
    AwaitingRelease,

    /// <summary>Exit open; waiting for the deck to clear.</summary>
    AwaitingExit,
    Fault,
}

public enum LightColor { Off, Red, Amber, Green }

public enum BarrierId { Entry, Exit }

public enum FaultKind
{
    None,
    CommunicationLost,
    Overload,
    AbnormalWeightChange,
    IdentificationConflict,
    IdentificationTimeout,
    PositionTimeout,
    StabilityTimeout,
    CaptureFailed,
    SafetyInterlock,
}

public enum AutomationEventKind
{
    StateChanged,
    VehicleDetected,
    VehicleIdentified,
    IncompleteWeighing,
    CaptureRequested,
    Captured,
    FaultRaised,
    FaultCleared,
    BarrierOpened,
    BarrierClosed,
    VehicleLeft,
    ExitOverdue,
    IndicatorUnavailable,
}

public enum EventSeverity { Info, Warning, Alarm }

public sealed record VehicleIdentification(string Registration, string Source, DateTimeOffset At, double? Confidence = null, string? RawValue = null);

public abstract record AutomationCommand;

public sealed record SetLightsCommand(LightColor Entry, LightColor Exit) : AutomationCommand;

public sealed record BarrierCommand(BarrierId Barrier, bool Open) : AutomationCommand;

public sealed record AlarmCommand(bool On) : AutomationCommand;

public sealed record CaptureRequest(Guid SessionId, VehicleIdentification? Vehicle) : AutomationCommand;

public sealed record PrintRequest(long TransactionId, bool Final) : AutomationCommand;

public sealed record AutomationEvent(AutomationEventKind Kind, EventSeverity Severity, string Message, Guid? SessionId) : AutomationCommand;

public sealed record AutomationInputs(
    DateTimeOffset Now,
    IndicatorSnapshot Indicator,
    bool? PresenceDetected = null,
    bool? PositionOk = null,
    bool SafetyOk = true);

public sealed record CaptureOutcome(bool Success, long? TransactionId, decimal? CapturedKg, bool IsFinal, string? Error);

public sealed record AutomationOptions
{
    public OperatingMode Mode { get; init; } = OperatingMode.SemiAutomatic;

    /// <summary>Deck is considered empty when the weight is within ±this.</summary>
    public decimal ZeroBandKg { get; init; } = 100m;

    /// <summary>A vehicle is considered on the deck from this weight.</summary>
    public decimal VehicleDetectKg { get; init; } = 500m;

    public bool UsePresenceSensor { get; init; }
    public bool RequireIdentification { get; init; } = true;
    public int IdentificationTimeoutMs { get; init; } = 20_000;

    /// <summary>Identifications read up to this long before the weight rose belong to the vehicle (ANPR at the entry).</summary>
    public int IdentificationLookbackMs { get; init; } = 20_000;

    public bool UsePositionSensors { get; init; }
    public int PositionTimeoutMs { get; init; } = 60_000;
    public int StableTimeoutMs { get; init; } = 90_000;
    public int CaptureTimeoutMs { get; init; } = 30_000;
    public int CommunicationLossMs { get; init; } = 3_000;
    public bool AutoRecoverCommunication { get; init; } = true;

    public bool PrintFirstWeighing { get; init; }
    public bool PrintOnCompletion { get; init; } = true;
    public bool AutoOpenExitBarrier { get; init; } = true;
    public bool HasExitBarrier { get; init; } = true;

    /// <summary>A weight increase beyond this after capture (e.g. a second vehicle driving on) raises a fault.</summary>
    public decimal AbnormalChangeKg { get; init; } = 300m;

    public int ExitOverdueMs { get; init; } = 300_000;

    public CaptureLimits Limits { get; init; } = new();
    public CapturePolicy Policy { get; init; } = new() { Unstable = UnstableCapturePolicy.Reject };
}
