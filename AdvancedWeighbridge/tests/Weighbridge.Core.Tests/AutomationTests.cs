using Weighbridge.Core.Automation;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Core.Tests;

public class AutomationTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly List<AutomationCommand> _log = [];

    private static AutomationOptions Auto(Func<AutomationOptions, AutomationOptions>? tweak = null)
    {
        var o = new AutomationOptions
        {
            Mode = OperatingMode.Automatic,
            Limits = new CaptureLimits { MinimumKg = 200, CapacityKg = 60000, FreshnessMs = 1500 },
        };
        return tweak is null ? o : tweak(o);
    }

    private IndicatorSnapshot Snap(decimal? kg, bool stable = true, ReadingFlags flags = ReadingFlags.None, ConnectionState c = ConnectionState.Connected) => new()
    {
        Connection = c,
        Latest = new WeightReading { Value = kg, Flags = flags, ReceivedAt = _now },
        Stability = new StabilityState(stable, stable ? StabilityReason.Stable : StabilityReason.OutsideTolerance, 0, stable ? 3000 : 0, 30, kg),
        Timestamp = _now,
    };

    private IReadOnlyList<AutomationCommand> Tick(AutomationEngine e, decimal? kg, bool stable = true, int advanceMs = 200, bool? presence = null, bool? position = null, bool safety = true,
        ReadingFlags flags = ReadingFlags.None, ConnectionState c = ConnectionState.Connected)
    {
        _now = _now.AddMilliseconds(advanceMs);
        var cmds = e.Tick(new AutomationInputs(_now, Snap(kg, stable, flags, c), presence, position, safety));
        _log.AddRange(cmds);
        return cmds;
    }

    private static VehicleIdentification Id(string reg, string source, DateTimeOffset at) => new(reg, source, at);

    [Fact]
    public void Full_automatic_cycle_with_anpr_read_before_weight_rises()
    {
        var e = new AutomationEngine(Auto());
        var first = Tick(e, 0);
        Assert.Contains(new SetLightsCommand(LightColor.Green, LightColor.Red), first);

        e.OnIdentification(Id("WP CAB-1234", "ANPR", _now), _now); // read at the entry
        Tick(e, 4000, stable: false);
        Assert.Equal(AutomationState.AwaitingStable, e.State);
        Assert.Equal("WP CAB-1234", e.Vehicle!.Registration);
        Assert.Contains(new SetLightsCommand(LightColor.Red, LightColor.Amber), _log);

        var cmds = Tick(e, 32500, stable: true);
        var request = Assert.Single(cmds.OfType<CaptureRequest>());
        Assert.Equal(AutomationState.Capturing, e.State);

        cmds = e.OnCaptureCompleted(new CaptureOutcome(true, 77, 32500, IsFinal: true, null), _now);
        Assert.Contains(new PrintRequest(77, true), cmds);
        Assert.Contains(new BarrierCommand(BarrierId.Exit, true), cmds);
        Assert.Contains(new SetLightsCommand(LightColor.Red, LightColor.Green), cmds);
        Assert.Equal(AutomationState.AwaitingExit, e.State);

        Tick(e, 15000, stable: false); // driving off
        cmds = Tick(e, 0);
        Assert.Contains(new BarrierCommand(BarrierId.Exit, false), cmds);
        Assert.Equal(AutomationState.Idle, e.State);
        Assert.Null(e.SessionId);
        Assert.Single(_log.OfType<CaptureRequest>()); // exactly one capture per vehicle session
    }

    [Fact]
    public void Never_captures_unstable_and_times_out_to_fault()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false, StableTimeoutMs = 1000 }));
        Tick(e, 0);
        for (var i = 0; i < 4; i++) Tick(e, 20000 + i * 100, stable: false);
        Assert.Empty(_log.OfType<CaptureRequest>());
        Tick(e, 20500, stable: false, advanceMs: 600);
        Assert.Equal(AutomationState.Fault, e.State);
        Assert.Equal(FaultKind.StabilityTimeout, e.Fault);
        Assert.Contains(new AlarmCommand(true), _log);
        Assert.Contains(new SetLightsCommand(LightColor.Red, LightColor.Red), _log);
    }

    [Fact]
    public void Waits_for_identification_and_operator_can_supply_it()
    {
        var e = new AutomationEngine(Auto(o => o with { IdentificationTimeoutMs = 1000 }));
        Tick(e, 0);
        Tick(e, 20000);
        Assert.Equal(AutomationState.AwaitingIdentification, e.State);
        Tick(e, 20000, advanceMs: 1200);
        Assert.Equal(FaultKind.IdentificationTimeout, e.Fault);

        e.OnIdentification(Id("NB-4455", "Operator", _now), _now);
        Assert.Equal(AutomationState.AwaitingStable, e.State);
        Assert.Equal(FaultKind.None, e.Fault);
        Assert.Single(Tick(e, 20000).OfType<CaptureRequest>());
    }

    [Fact]
    public void Conflicting_identifications_raise_fault()
    {
        var e = new AutomationEngine(Auto());
        Tick(e, 0);
        e.OnIdentification(Id("AAA-1111", "RFID", _now), _now);
        Tick(e, 20000, stable: false);
        e.OnIdentification(Id("BBB-2222", "ANPR", _now), _now);
        Assert.Equal(FaultKind.IdentificationConflict, e.Fault);
    }

    [Fact]
    public void Vehicle_leaving_before_capture_is_an_incomplete_weighing()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false }));
        Tick(e, 0);
        Tick(e, 9000, stable: false);
        Tick(e, 50, stable: false);
        Assert.Equal(AutomationState.Idle, e.State);
        Assert.Contains(_log.OfType<AutomationEvent>(), ev => ev.Kind == AutomationEventKind.IncompleteWeighing);
        Assert.Empty(_log.OfType<CaptureRequest>());
    }

    [Fact]
    public void Second_vehicle_driving_on_while_exiting_is_detected()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false }));
        Tick(e, 0);
        Tick(e, 20000);
        Assert.Single(Tick(e, 20000).OfType<CaptureRequest>());
        e.OnCaptureCompleted(new CaptureOutcome(true, 5, 20000, true, null), _now);
        Assert.Equal(AutomationState.AwaitingExit, e.State);
        var cmds = Tick(e, 26000, stable: false);
        Assert.Equal(FaultKind.AbnormalWeightChange, e.Fault);
        Assert.Contains(new BarrierCommand(BarrierId.Exit, false), cmds);

        // An abnormal change needs an operator, even after the platform clears.
        Tick(e, 0);
        Assert.Equal(AutomationState.Fault, e.State);
        e.OperatorAcknowledge(_now);
        Assert.Equal(AutomationState.Idle, e.State);
    }

    [Fact]
    public void Communication_loss_faults_and_recovers_automatically()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false, CommunicationLossMs = 1000 }));
        Tick(e, 0);
        Tick(e, 20000, stable: false);
        Tick(e, null, c: ConnectionState.Silent, advanceMs: 1500);
        Assert.Equal(FaultKind.CommunicationLost, e.Fault);
        Tick(e, 20000, stable: false);
        Assert.Equal(AutomationState.AwaitingStable, e.State);
    }

    [Fact]
    public void Idle_with_dead_indicator_holds_entry_red()
    {
        var e = new AutomationEngine(Auto());
        var cmds = Tick(e, null, c: ConnectionState.Disconnected);
        Assert.Contains(new SetLightsCommand(LightColor.Red, LightColor.Red), cmds);
        Assert.Contains(cmds.OfType<AutomationEvent>(), ev => ev.Kind == AutomationEventKind.IndicatorUnavailable);
    }

    [Fact]
    public void Overload_is_a_fault_and_clears_when_empty()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false }));
        Tick(e, 0);
        Tick(e, null, flags: ReadingFlags.Overload);
        Assert.Equal(FaultKind.Overload, e.Fault);
        Tick(e, 0);
        Assert.Equal(AutomationState.Idle, e.State);
    }

    [Fact]
    public void Position_sensors_gate_the_capture()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false, UsePositionSensors = true }));
        Tick(e, 0);
        Tick(e, 20000, position: false);
        Assert.Equal(AutomationState.AwaitingPosition, e.State);
        Tick(e, 20000, position: false);
        Assert.Empty(_log.OfType<CaptureRequest>());
        Tick(e, 20000, position: true);
        Assert.Equal(AutomationState.AwaitingStable, e.State);
        Assert.Single(Tick(e, 20000, position: true).OfType<CaptureRequest>());
    }

    [Fact]
    public void Exit_is_not_opened_when_safety_interlock_fails()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false }));
        Tick(e, 0);
        Tick(e, 20000, safety: false);
        Tick(e, 20000, safety: false);
        var cmds = e.OnCaptureCompleted(new CaptureOutcome(true, 9, 20000, true, null), _now);
        Assert.DoesNotContain(cmds.OfType<BarrierCommand>(), b => b.Open);
        Assert.Equal(AutomationState.AwaitingRelease, e.State);

        Tick(e, 20000, safety: true);
        cmds = e.OperatorRelease(_now);
        Assert.Contains(new BarrierCommand(BarrierId.Exit, true), cmds);
    }

    [Fact]
    public void Failed_capture_never_opens_the_barrier()
    {
        var e = new AutomationEngine(Auto(o => o with { RequireIdentification = false }));
        Tick(e, 0);
        Tick(e, 20000);
        Tick(e, 20000);
        var cmds = e.OnCaptureCompleted(new CaptureOutcome(false, null, null, false, "Vehicle is blacklisted."), _now);
        Assert.Equal(FaultKind.CaptureFailed, e.Fault);
        Assert.DoesNotContain(cmds.OfType<BarrierCommand>(), b => b.Open);
        Assert.DoesNotContain(e.OperatorRelease(_now).OfType<BarrierCommand>(), b => b.Open);
        // An authorised manual override can release, and that path is explicit.
        Assert.Contains(new BarrierCommand(BarrierId.Exit, true), e.OperatorRelease(_now, manualOverride: true, overrideAuthorized: true));
    }

    [Fact]
    public void Semi_automatic_waits_for_operator_capture_and_release()
    {
        var e = new AutomationEngine(Auto(o => o with { Mode = OperatingMode.SemiAutomatic }));
        Tick(e, 0);
        Tick(e, 20000);
        Tick(e, 20000);
        Assert.Equal(AutomationState.AwaitingStable, e.State);
        Assert.Empty(_log.OfType<CaptureRequest>());

        var cmds = e.OnCaptureCompleted(new CaptureOutcome(true, 3, 20000, false, null), _now);
        Assert.Empty(cmds.OfType<PrintRequest>());
        Assert.Empty(cmds.OfType<BarrierCommand>());
        Assert.Equal(AutomationState.AwaitingRelease, e.State);
        Assert.Contains(new BarrierCommand(BarrierId.Exit, true), e.OperatorRelease(_now));
    }

    [Fact]
    public void Manual_mode_disables_automation()
    {
        var e = new AutomationEngine(Auto());
        Tick(e, 0);
        var cmds = e.SetOptions(e.Options with { Mode = OperatingMode.Manual }, _now);
        Assert.Equal(AutomationState.Disabled, e.State);
        Assert.Contains(new SetLightsCommand(LightColor.Off, LightColor.Off), cmds);
        Assert.Empty(Tick(e, 20000));
    }

    [Fact]
    public void Interlock_rules()
    {
        Assert.False(BarrierInterlock.CanOpenExit(new(CaptureSaved: false, VehicleOnDeck: true, SafetyOk: true, FaultKind.None)).Allowed);
        Assert.False(BarrierInterlock.CanOpenExit(new(true, VehicleOnDeck: false, true, FaultKind.None)).Allowed);
        Assert.False(BarrierInterlock.CanOpenExit(new(true, true, SafetyOk: false, FaultKind.None, ManualOverride: true, OverrideAuthorized: true)).Allowed);
        Assert.False(BarrierInterlock.CanOpenExit(new(true, true, true, FaultKind.Overload)).Allowed);
        Assert.False(BarrierInterlock.CanOpenExit(new(false, false, true, FaultKind.None, ManualOverride: true, OverrideAuthorized: false)).Allowed);
        Assert.True(BarrierInterlock.CanOpenExit(new(true, true, true, FaultKind.None)).Allowed);
        Assert.True(BarrierInterlock.CanOpenEntry(new(DeckEmpty: true, IndicatorHealthy: true, SafetyOk: true, FaultKind.None)).Allowed);
        Assert.False(BarrierInterlock.CanOpenEntry(new(DeckEmpty: false, IndicatorHealthy: true, SafetyOk: true, FaultKind.None)).Allowed);
    }
}
