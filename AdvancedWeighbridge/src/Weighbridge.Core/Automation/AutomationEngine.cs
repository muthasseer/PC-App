using Weighbridge.Core.Indicators;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Core.Automation;

/// <summary>
/// Deterministic state machine for automatic and semi-automatic operation. It has no I/O and no clock of its own:
/// the controller feeds inputs (indicator snapshot, sensors, identifications, capture results) and executes the
/// returned commands. Every transition produces an <see cref="AutomationEvent"/> for the audit log.
/// </summary>
public sealed class AutomationEngine
{
    private readonly List<VehicleIdentification> _recentIds = [];
    private AutomationOptions _options;
    private AutomationInputs? _last;
    private DateTimeOffset _stateSince;
    private DateTimeOffset _lastHealthyAt = DateTimeOffset.MinValue;
    private (LightColor Entry, LightColor Exit)? _lights;
    private bool? _alarm;
    private bool _exitOpen;
    private bool _unavailableNotified;
    private bool _overdueNotified;

    public AutomationEngine(AutomationOptions options)
    {
        _options = options;
        State = options.Mode == OperatingMode.Manual ? AutomationState.Disabled : AutomationState.Idle;
    }

    public AutomationOptions Options => _options;
    public AutomationState State { get; private set; }
    public FaultKind Fault { get; private set; }
    public string? FaultMessage { get; private set; }
    public Guid? SessionId { get; private set; }
    public DateTimeOffset? SessionStartedAt { get; private set; }
    public VehicleIdentification? Vehicle { get; private set; }
    public decimal? CapturedKg { get; private set; }
    public long? TransactionId { get; private set; }
    public bool CaptureFinal { get; private set; }
    public bool ExitOpen => _exitOpen;
    public (LightColor Entry, LightColor Exit) Lights => _lights ?? (LightColor.Off, LightColor.Off);

    public IReadOnlyList<AutomationCommand> SetOptions(AutomationOptions options, DateTimeOffset now)
    {
        var cmds = new List<AutomationCommand>();
        var modeChanged = options.Mode != _options.Mode;
        _options = options;
        if (!modeChanged) return cmds;

        if (options.Mode == OperatingMode.Manual)
        {
            EndSession();
            ClearFault();
            Transition(AutomationState.Disabled, now, "Manual mode selected.", cmds);
            SetLights(LightColor.Off, LightColor.Off, cmds);
            SetAlarm(false, cmds);
        }
        else if (State == AutomationState.Disabled)
        {
            Transition(AutomationState.Idle, now, $"{options.Mode} mode selected.", cmds);
        }
        return cmds;
    }

    // ------------------------------------------------------------------ inputs

    public IReadOnlyList<AutomationCommand> Tick(AutomationInputs inputs)
    {
        _last = inputs;
        var cmds = new List<AutomationCommand>();
        if (_options.Mode == OperatingMode.Manual)
        {
            if (State != AutomationState.Disabled) Transition(AutomationState.Disabled, inputs.Now, "Manual mode.", cmds);
            return cmds;
        }

        var now = inputs.Now;
        var reading = inputs.Indicator.Latest;
        var healthy = inputs.Indicator.Connection == ConnectionState.Connected && reading is not null
                      && (now - reading.ReceivedAt).TotalMilliseconds <= _options.CommunicationLossMs;
        if (healthy) _lastHealthyAt = now;
        var commLost = !healthy && (_lastHealthyAt == DateTimeOffset.MinValue || (now - _lastHealthyAt).TotalMilliseconds >= _options.CommunicationLossMs);
        var kg = healthy && reading!.IsUsable ? reading.Weight!.Value.Kilograms : (decimal?)null;
        var overload = healthy && (reading!.IsOverload || (kg is { } k0 && k0 > _options.Limits.CapacityKg));
        var occupied = (kg is { } k1 && k1 >= _options.VehicleDetectKg) || (_options.UsePresenceSensor && inputs.PresenceDetected == true);
        var empty = kg is { } k2 && Math.Abs(k2) <= _options.ZeroBandKg && inputs.PresenceDetected != true;
        var elapsed = (now - _stateSince).TotalMilliseconds;

        switch (State)
        {
            case AutomationState.Disabled:
                Transition(AutomationState.Idle, now, "Automation enabled.", cmds);
                goto case AutomationState.Idle;

            case AutomationState.Idle:
                if (!healthy)
                {
                    SetLights(LightColor.Red, LightColor.Red, cmds);
                    if (!_unavailableNotified)
                    {
                        _unavailableNotified = true;
                        cmds.Add(new AutomationEvent(AutomationEventKind.IndicatorUnavailable, EventSeverity.Warning, "Indicator unavailable – entry held at red.", null));
                    }
                    break;
                }
                _unavailableNotified = false;
                if (overload)
                {
                    RaiseFault(FaultKind.Overload, "Overload with no vehicle session.", now, cmds);
                    break;
                }
                if (occupied)
                {
                    StartSession(now, cmds);
                    AdvanceAfterDetection(now, cmds);
                    break;
                }
                SetLights(LightColor.Green, LightColor.Red, cmds);
                SetAlarm(false, cmds);
                break;

            case AutomationState.AwaitingIdentification:
                if (empty) { Incomplete("Vehicle left before it was identified.", now, cmds); break; }
                if (commLost) { RaiseFault(FaultKind.CommunicationLost, "Indicator communication lost.", now, cmds); break; }
                if (Vehicle is not null) { AdvanceAfterIdentification(now, cmds); break; }
                if (elapsed >= _options.IdentificationTimeoutMs) RaiseFault(FaultKind.IdentificationTimeout, "Vehicle could not be identified. Enter the vehicle number.", now, cmds);
                break;

            case AutomationState.AwaitingPosition:
                if (empty) { Incomplete("Vehicle left before it was positioned.", now, cmds); break; }
                if (commLost) { RaiseFault(FaultKind.CommunicationLost, "Indicator communication lost.", now, cmds); break; }
                if (inputs.PositionOk == true) { Transition(AutomationState.AwaitingStable, now, "Vehicle fully on the platform.", cmds); break; }
                if (elapsed >= _options.PositionTimeoutMs) RaiseFault(FaultKind.PositionTimeout, "Vehicle is not fully on the platform.", now, cmds);
                break;

            case AutomationState.AwaitingStable:
                if (empty) { Incomplete("Vehicle left the platform before the weight was captured.", now, cmds); break; }
                if (commLost) { RaiseFault(FaultKind.CommunicationLost, "Indicator communication lost.", now, cmds); break; }
                if (overload) { RaiseFault(FaultKind.Overload, "Weighbridge overload.", now, cmds); break; }
                if (_options.UsePositionSensors && inputs.PositionOk == false)
                {
                    Transition(AutomationState.AwaitingPosition, now, "Vehicle moved off the platform edge.", cmds);
                    break;
                }
                if (_options.Mode == OperatingMode.Automatic)
                {
                    var decision = CaptureValidator.Evaluate(inputs.Indicator, now, _options.Limits, _options.Policy with { Unstable = UnstableCapturePolicy.Reject });
                    if (decision.IsAllowed)
                    {
                        Transition(AutomationState.Capturing, now, "Stable weight – capturing.", cmds);
                        cmds.Add(new CaptureRequest(SessionId!.Value, Vehicle));
                        cmds.Add(new AutomationEvent(AutomationEventKind.CaptureRequested, EventSeverity.Info, $"Capture requested at {decision.Reading!.Weight!.Value.Kilograms:0.###} kg.", SessionId));
                        break;
                    }
                    if (elapsed >= _options.StableTimeoutMs) RaiseFault(FaultKind.StabilityTimeout, "Weight did not become stable: " + decision.Summary, now, cmds);
                }
                break;

            case AutomationState.Capturing:
                if (elapsed >= _options.CaptureTimeoutMs) RaiseFault(FaultKind.CaptureFailed, "Capture did not complete in time.", now, cmds);
                break;

            case AutomationState.AwaitingRelease:
                if (kg is { } kr && CapturedKg is { } cr && Math.Abs(kr - cr) > _options.AbnormalChangeKg && !empty)
                {
                    RaiseFault(FaultKind.AbnormalWeightChange, $"Weight changed from {cr:0.###} kg to {kr:0.###} kg after capture.", now, cmds);
                    break;
                }
                if (empty)
                {
                    cmds.Add(new AutomationEvent(AutomationEventKind.VehicleLeft, EventSeverity.Info, "Vehicle left the platform.", SessionId));
                    EndSession();
                    Transition(AutomationState.Idle, now, "Platform empty.", cmds);
                    SetLights(LightColor.Green, LightColor.Red, cmds);
                }
                break;

            case AutomationState.AwaitingExit:
                if (kg is { } ke && CapturedKg is { } ce && ke - ce > _options.AbnormalChangeKg)
                {
                    CloseExit(cmds, "Abnormal weight increase while exiting.");
                    RaiseFault(FaultKind.AbnormalWeightChange, $"Weight increased from {ce:0.###} kg to {ke:0.###} kg while the vehicle was leaving (second vehicle on the platform?).", now, cmds);
                    break;
                }
                if (empty)
                {
                    CloseExit(cmds, "Vehicle left the platform.");
                    cmds.Add(new AutomationEvent(AutomationEventKind.VehicleLeft, EventSeverity.Info, "Vehicle left the platform.", SessionId));
                    EndSession();
                    Transition(AutomationState.Idle, now, "Platform empty.", cmds);
                    SetLights(LightColor.Green, LightColor.Red, cmds);
                    break;
                }
                if (!_overdueNotified && elapsed >= _options.ExitOverdueMs)
                {
                    _overdueNotified = true;
                    cmds.Add(new AutomationEvent(AutomationEventKind.ExitOverdue, EventSeverity.Warning, "Vehicle has not left the platform.", SessionId));
                }
                break;

            case AutomationState.Fault:
                SetLights(LightColor.Red, LightColor.Red, cmds);
                SetAlarm(true, cmds);
                if (Fault == FaultKind.CommunicationLost && _options.AutoRecoverCommunication && healthy)
                {
                    ClearFault();
                    cmds.Add(new AutomationEvent(AutomationEventKind.FaultCleared, EventSeverity.Info, "Indicator communication restored.", SessionId));
                    SetAlarm(false, cmds);
                    ResumeSession(now, cmds, "Communication restored.");
                    break;
                }
                if (empty && Fault is FaultKind.IdentificationTimeout or FaultKind.PositionTimeout or FaultKind.StabilityTimeout or FaultKind.Overload or FaultKind.CaptureFailed or FaultKind.IdentificationConflict)
                {
                    if (CapturedKg is null) cmds.Add(new AutomationEvent(AutomationEventKind.IncompleteWeighing, EventSeverity.Warning, "Vehicle left during a fault without a complete weighing.", SessionId));
                    ClearFault();
                    cmds.Add(new AutomationEvent(AutomationEventKind.FaultCleared, EventSeverity.Info, "Fault cleared: platform empty.", SessionId));
                    SetAlarm(false, cmds);
                    EndSession();
                    Transition(AutomationState.Idle, now, "Platform empty.", cmds);
                    SetLights(LightColor.Green, LightColor.Red, cmds);
                }
                break;
        }

        return cmds;
    }

    public IReadOnlyList<AutomationCommand> OnIdentification(VehicleIdentification id, DateTimeOffset now)
    {
        var cmds = new List<AutomationCommand>();
        _recentIds.Add(id);
        if (_recentIds.Count > 20) _recentIds.RemoveAt(0);
        if (SessionId is null || State is AutomationState.Idle or AutomationState.Disabled) return cmds;

        if (Vehicle is null)
        {
            Vehicle = id;
            cmds.Add(new AutomationEvent(AutomationEventKind.VehicleIdentified, EventSeverity.Info, $"Vehicle {id.Registration} identified by {id.Source}.", SessionId));
            if (State == AutomationState.Fault && Fault == FaultKind.IdentificationTimeout)
            {
                ClearFault();
                SetAlarm(false, cmds);
                cmds.Add(new AutomationEvent(AutomationEventKind.FaultCleared, EventSeverity.Info, "Vehicle identified.", SessionId));
                AdvanceAfterIdentification(now, cmds);
            }
            else if (State == AutomationState.AwaitingIdentification)
            {
                AdvanceAfterIdentification(now, cmds);
            }
            return cmds;
        }

        if (VehicleRegistration.Normalize(id.Registration) == VehicleRegistration.Normalize(Vehicle.Registration)) return cmds;

        if (string.Equals(id.Source, "Operator", StringComparison.OrdinalIgnoreCase) && CapturedKg is null)
        {
            cmds.Add(new AutomationEvent(AutomationEventKind.VehicleIdentified, EventSeverity.Warning, $"Operator corrected the vehicle from {Vehicle.Registration} to {id.Registration}.", SessionId));
            Vehicle = id;
            if (State == AutomationState.Fault && Fault == FaultKind.IdentificationConflict)
            {
                ClearFault();
                SetAlarm(false, cmds);
                AdvanceAfterIdentification(now, cmds);
            }
            return cmds;
        }

        if (State is not (AutomationState.AwaitingExit or AutomationState.AwaitingRelease))
        {
            RaiseFault(FaultKind.IdentificationConflict, $"Two different vehicles identified: {Vehicle.Registration} and {id.Registration}.", now, cmds);
        }
        return cmds;
    }

    public IReadOnlyList<AutomationCommand> OnCaptureCompleted(CaptureOutcome outcome, DateTimeOffset now)
    {
        var cmds = new List<AutomationCommand>();
        var accepts = State == AutomationState.Capturing
                      || (_options.Mode == OperatingMode.SemiAutomatic && State is AutomationState.Idle or AutomationState.AwaitingStable or AutomationState.AwaitingPosition or AutomationState.AwaitingIdentification);
        if (!accepts)
        {
            cmds.Add(new AutomationEvent(AutomationEventKind.Captured, EventSeverity.Warning, $"Capture result ignored in state {State}.", SessionId));
            return cmds;
        }

        if (!outcome.Success)
        {
            RaiseFault(FaultKind.CaptureFailed, outcome.Error ?? "Capture failed.", now, cmds);
            return cmds;
        }

        if (SessionId is null) StartSession(now, cmds);
        CapturedKg = outcome.CapturedKg;
        TransactionId = outcome.TransactionId;
        CaptureFinal = outcome.IsFinal;
        cmds.Add(new AutomationEvent(AutomationEventKind.Captured, EventSeverity.Info,
            $"{(outcome.IsFinal ? "Second" : "First")} weight captured: {outcome.CapturedKg:0.###} kg (transaction {outcome.TransactionId}).", SessionId));

        if (_options.Mode == OperatingMode.Automatic && outcome.TransactionId is { } txn
            && ((outcome.IsFinal && _options.PrintOnCompletion) || (!outcome.IsFinal && _options.PrintFirstWeighing)))
        {
            cmds.Add(new PrintRequest(txn, outcome.IsFinal));
        }

        if (_options.Mode == OperatingMode.Automatic && _options.AutoOpenExitBarrier)
        {
            TryRelease(now, cmds, manualOverride: false, authorized: false);
        }
        else
        {
            Transition(AutomationState.AwaitingRelease, now, "Waiting for the operator to release the vehicle.", cmds);
            SetLights(LightColor.Red, LightColor.Amber, cmds);
        }
        return cmds;
    }

    /// <summary>Operator releases the vehicle (opens the exit). Interlocks are always evaluated.</summary>
    public IReadOnlyList<AutomationCommand> OperatorRelease(DateTimeOffset now, bool manualOverride = false, bool overrideAuthorized = false)
    {
        var cmds = new List<AutomationCommand>();
        if (State is not (AutomationState.AwaitingRelease or AutomationState.Fault) && !manualOverride)
        {
            cmds.Add(new AutomationEvent(AutomationEventKind.BarrierOpened, EventSeverity.Warning, $"Release refused in state {State}.", SessionId));
            return cmds;
        }
        TryRelease(now, cmds, manualOverride, overrideAuthorized);
        return cmds;
    }

    public IReadOnlyList<AutomationCommand> OperatorAcknowledge(DateTimeOffset now)
    {
        var cmds = new List<AutomationCommand>();
        if (State != AutomationState.Fault) return cmds;
        var fault = Fault;
        ClearFault();
        SetAlarm(false, cmds);
        cmds.Add(new AutomationEvent(AutomationEventKind.FaultCleared, EventSeverity.Info, $"Operator acknowledged fault {fault}.", SessionId));

        var kg = _last?.Indicator.Latest is { IsUsable: true } r ? r.Weight!.Value.Kilograms : (decimal?)null;
        var empty = kg is { } k && Math.Abs(k) <= _options.ZeroBandKg && _last?.PresenceDetected != true;
        if (empty || SessionId is null)
        {
            EndSession();
            Transition(AutomationState.Idle, now, "Fault acknowledged, platform empty.", cmds);
            SetLights(LightColor.Green, LightColor.Red, cmds);
            return cmds;
        }
        ResumeSession(now, cmds, "Fault acknowledged.");
        return cmds;
    }

    // ------------------------------------------------------------------ helpers

    private void StartSession(DateTimeOffset now, List<AutomationCommand> cmds)
    {
        SessionId = Guid.NewGuid();
        SessionStartedAt = now;
        CapturedKg = null;
        TransactionId = null;
        CaptureFinal = false;
        _overdueNotified = false;
        Vehicle = _recentIds.LastOrDefault(i => (now - i.At).TotalMilliseconds <= _options.IdentificationLookbackMs && i.At <= now.AddSeconds(1));
        cmds.Add(new AutomationEvent(AutomationEventKind.VehicleDetected, EventSeverity.Info,
            Vehicle is null ? "Vehicle detected on the platform." : $"Vehicle {Vehicle.Registration} detected on the platform.", SessionId));
    }

    private void AdvanceAfterDetection(DateTimeOffset now, List<AutomationCommand> cmds)
    {
        SetLights(LightColor.Red, LightColor.Amber, cmds);
        if (_options.Mode == OperatingMode.Automatic && _options.RequireIdentification && Vehicle is null)
        {
            Transition(AutomationState.AwaitingIdentification, now, "Waiting for vehicle identification.", cmds);
            return;
        }
        AdvanceAfterIdentification(now, cmds);
    }

    private void AdvanceAfterIdentification(DateTimeOffset now, List<AutomationCommand> cmds)
    {
        SetLights(LightColor.Red, LightColor.Amber, cmds);
        if (_options.UsePositionSensors && _last?.PositionOk != true)
        {
            Transition(AutomationState.AwaitingPosition, now, "Waiting for the vehicle to be fully on the platform.", cmds);
            return;
        }
        Transition(AutomationState.AwaitingStable, now, "Waiting for a stable weight.", cmds);
    }

    private void ResumeSession(DateTimeOffset now, List<AutomationCommand> cmds, string reason)
    {
        if (CapturedKg is not null)
        {
            Transition(AutomationState.AwaitingRelease, now, reason + " Waiting for release.", cmds);
            SetLights(LightColor.Red, LightColor.Amber, cmds);
            return;
        }
        if (_options.Mode == OperatingMode.Automatic && _options.RequireIdentification && Vehicle is null)
        {
            Transition(AutomationState.AwaitingIdentification, now, reason, cmds);
            SetLights(LightColor.Red, LightColor.Amber, cmds);
            return;
        }
        AdvanceAfterIdentification(now, cmds);
    }

    private void TryRelease(DateTimeOffset now, List<AutomationCommand> cmds, bool manualOverride, bool authorized)
    {
        var kg = _last?.Indicator.Latest is { IsUsable: true } r ? r.Weight!.Value.Kilograms : (decimal?)null;
        var onDeck = (kg is { } k && k > _options.ZeroBandKg) || _last?.PresenceDetected == true;
        var result = BarrierInterlock.CanOpenExit(new ExitInterlockContext(
            CaptureSaved: TransactionId is not null,
            VehicleOnDeck: onDeck,
            SafetyOk: _last?.SafetyOk ?? true,
            ActiveFault: Fault,
            ManualOverride: manualOverride,
            OverrideAuthorized: authorized));

        if (!result.Allowed)
        {
            cmds.Add(new AutomationEvent(AutomationEventKind.BarrierOpened, EventSeverity.Warning, "Exit not opened: " + result.Reason, SessionId));
            if (State != AutomationState.AwaitingRelease && State != AutomationState.Fault)
            {
                Transition(AutomationState.AwaitingRelease, now, "Exit held: " + result.Reason, cmds);
            }
            SetLights(LightColor.Red, State == AutomationState.Fault ? LightColor.Red : LightColor.Amber, cmds);
            return;
        }

        if (manualOverride && Fault != FaultKind.None)
        {
            ClearFault();
            SetAlarm(false, cmds);
        }
        if (_options.HasExitBarrier)
        {
            cmds.Add(new BarrierCommand(BarrierId.Exit, true));
            _exitOpen = true;
            cmds.Add(new AutomationEvent(AutomationEventKind.BarrierOpened, manualOverride ? EventSeverity.Warning : EventSeverity.Info,
                "Exit barrier opened: " + result.Reason, SessionId));
        }
        Transition(AutomationState.AwaitingExit, now, "Vehicle released.", cmds);
        SetLights(LightColor.Red, LightColor.Green, cmds);
    }

    private void CloseExit(List<AutomationCommand> cmds, string reason)
    {
        if (!_exitOpen || !_options.HasExitBarrier) return;
        cmds.Add(new BarrierCommand(BarrierId.Exit, false));
        _exitOpen = false;
        cmds.Add(new AutomationEvent(AutomationEventKind.BarrierClosed, EventSeverity.Info, "Exit barrier closed: " + reason, SessionId));
    }

    private void Incomplete(string message, DateTimeOffset now, List<AutomationCommand> cmds)
    {
        cmds.Add(new AutomationEvent(AutomationEventKind.IncompleteWeighing, EventSeverity.Warning, message, SessionId));
        EndSession();
        Transition(AutomationState.Idle, now, "Platform empty.", cmds);
        SetLights(LightColor.Green, LightColor.Red, cmds);
    }

    private void RaiseFault(FaultKind fault, string message, DateTimeOffset now, List<AutomationCommand> cmds)
    {
        Fault = fault;
        FaultMessage = message;
        cmds.Add(new AutomationEvent(AutomationEventKind.FaultRaised, EventSeverity.Alarm, $"{fault}: {message}", SessionId));
        Transition(AutomationState.Fault, now, message, cmds);
        SetLights(LightColor.Red, LightColor.Red, cmds);
        SetAlarm(true, cmds);
    }

    private void ClearFault()
    {
        Fault = FaultKind.None;
        FaultMessage = null;
    }

    private void EndSession()
    {
        SessionId = null;
        SessionStartedAt = null;
        Vehicle = null;
        CapturedKg = null;
        TransactionId = null;
        CaptureFinal = false;
        _recentIds.Clear();
    }

    private void Transition(AutomationState to, DateTimeOffset now, string reason, List<AutomationCommand> cmds)
    {
        if (State == to) return;
        var from = State;
        State = to;
        _stateSince = now;
        cmds.Add(new AutomationEvent(AutomationEventKind.StateChanged, EventSeverity.Info, $"{from} → {to}: {reason}", SessionId));
    }

    private void SetLights(LightColor entry, LightColor exit, List<AutomationCommand> cmds)
    {
        if (_lights == (entry, exit)) return;
        _lights = (entry, exit);
        cmds.Add(new SetLightsCommand(entry, exit));
    }

    private void SetAlarm(bool on, List<AutomationCommand> cmds)
    {
        if (_alarm == on) return;
        _alarm = on;
        cmds.Add(new AlarmCommand(on));
    }
}
