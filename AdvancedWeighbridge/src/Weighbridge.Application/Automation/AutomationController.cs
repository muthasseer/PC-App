using Microsoft.Extensions.Logging;
using Weighbridge.Application.Hardware;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Automation;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Devices.IO;

namespace Weighbridge.Application.Automation;

public sealed record AutomationStatus(
    OperatingMode Mode,
    AutomationState State,
    FaultKind Fault,
    string? FaultMessage,
    LightColor EntryLight,
    LightColor ExitLight,
    bool ExitOpen,
    VehicleIdentification? Vehicle,
    decimal? CapturedKg,
    long? TransactionId,
    SensorState? Sensors,
    string? LastEvent);

/// <summary>
/// Drives the <see cref="AutomationEngine"/>: every 100 ms it feeds the indicator snapshot and sensor states,
/// executes the resulting commands on the I/O hardware, performs automatic captures through the
/// <see cref="WeighingService"/> and writes every event to the audit log. All engine calls are serialised.
/// </summary>
public sealed class AutomationController : IAsyncDisposable
{
    private readonly IndicatorManager _indicator;
    private readonly HardwareController? _hardware;
    private readonly WeighingService _weighing;
    private readonly CameraService _cameras;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AutomationEngine _engine = new(new AutomationOptions { Mode = OperatingMode.Manual });
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SensorState? _sensors;
    private DateTimeOffset _lastSensorPoll = DateTimeOffset.MinValue;
    private UserContext _actor = UserContext.System;
    private string? _lastEvent;
    private int _inputPollMs = 200;

    public AutomationController(IndicatorManager indicator, HardwareController? hardware, WeighingService weighing, CameraService cameras, IdentificationService identification,
        SettingsService settings, AuditService audit, NotificationService notifications, TimeProvider time, ILogger<AutomationController> log)
    {
        _indicator = indicator;
        _hardware = hardware;
        _weighing = weighing;
        _cameras = cameras;
        _settings = settings;
        _audit = audit;
        _notifications = notifications;
        _time = time;
        _log = log;
        identification.Identified += (_, id) => _ = OnIdentifiedAsync(id);
    }

    public event EventHandler<AutomationStatus>? StatusChanged;

    /// <summary>An automatic capture finished (success or failure).</summary>
    public event EventHandler<CaptureResult>? AutoCaptured;

    /// <summary>The engine asks for a ticket to be printed (desktop prints it).</summary>
    public event EventHandler<PrintRequest>? PrintRequested;

    public OperatingMode Mode => _engine.Options.Mode;

    public AutomationStatus Status => new(_engine.Options.Mode, _engine.State, _engine.Fault, _engine.FaultMessage, _engine.Lights.Entry, _engine.Lights.Exit, _engine.ExitOpen,
        _engine.Vehicle, _engine.CapturedKg, _engine.TransactionId, _sensors, _lastEvent);

    public async Task StartAsync(OperatingMode mode, UserContext actor, CancellationToken ct = default)
    {
        _actor = actor;
        _engine = new AutomationEngine(await OptionsAsync(mode, ct).ConfigureAwait(false));
        _inputPollMs = _hardware?.Config.InputPollMs ?? 200;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Changes the operating mode. Automatic mode requires its own permission and is audited.</summary>
    public async Task SetModeAsync(UserContext user, OperatingMode mode, CancellationToken ct = default)
    {
        if (mode == OperatingMode.Automatic) user.Demand(Permissions.WeighingAutomatic);
        else if (mode == OperatingMode.Manual) user.Demand(Permissions.WeighingManualEntry);
        else user.Demand(Permissions.WeighingCapture);
        var options = await OptionsAsync(mode, ct).ConfigureAwait(false);
        var previous = _engine.Options.Mode;
        await Locked(() => _engine.SetOptions(options, _time.GetUtcNow()), ct).ConfigureAwait(false);
        _actor = user;
        if (previous != mode) await _audit.WriteAsync(user, "Automation", "ModeChanged", $"Operating mode changed from {previous} to {mode}", ct: ct).ConfigureAwait(false);
    }

    /// <summary>Re-reads settings (limits, hardware, automation thresholds) without changing the mode.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var options = await OptionsAsync(_engine.Options.Mode, ct).ConfigureAwait(false);
        await Locked(() => _engine.SetOptions(options, _time.GetUtcNow()), ct).ConfigureAwait(false);
    }

    /// <summary>Semi-automatic: the operator's GET WEIGHT result keeps the engine (lights, release) in step.</summary>
    public Task NotifyOperatorCaptureAsync(CaptureResult result, CancellationToken ct = default) =>
        Locked(() => _engine.OnCaptureCompleted(new CaptureOutcome(result.Success, result.Transaction?.Id, result.Measurement?.Weight.Kilograms, result.IsFinal, result.Success ? null : result.Message), _time.GetUtcNow()), ct);

    public async Task ReleaseVehicleAsync(UserContext user, bool manualOverride = false, CancellationToken ct = default)
    {
        user.Demand(manualOverride ? Permissions.HardwareOverride : Permissions.WeighingCapture);
        await Locked(() => _engine.OperatorRelease(_time.GetUtcNow(), manualOverride, user.Has(Permissions.HardwareOverride)), ct).ConfigureAwait(false);
        await _audit.WriteAsync(user, "Automation", manualOverride ? "ReleaseOverride" : "Release", manualOverride ? "Vehicle released by manual override" : "Vehicle released by the operator", ct: ct).ConfigureAwait(false);
    }

    public async Task AcknowledgeFaultAsync(UserContext user, CancellationToken ct = default)
    {
        user.Demand(Permissions.WeighingCapture);
        var fault = _engine.Fault;
        await Locked(() => _engine.OperatorAcknowledge(_time.GetUtcNow()), ct).ConfigureAwait(false);
        await _audit.WriteAsync(user, "Automation", "FaultAcknowledged", $"Fault {fault} acknowledged", ct: ct).ConfigureAwait(false);
    }

    /// <summary>Manual light/barrier control (maintenance, emergencies). Opening still requires the safety input.</summary>
    public async Task ManualBarrierAsync(UserContext user, BarrierId barrier, bool open, string reason, CancellationToken ct = default)
    {
        user.Demand(Permissions.HardwareOverride);
        if (_hardware is null) throw new InvalidOperationException("No I/O hardware is configured.");
        if (open)
        {
            var sensors = await _hardware.ReadSensorsAsync(ct).ConfigureAwait(false);
            if (!sensors.SafetyOk) throw new InvalidOperationException("The safety interlock is not satisfied; the barrier cannot be opened.");
        }
        await _hardware.SetBarrierAsync(barrier, open, ct).ConfigureAwait(false);
        await _audit.WriteAsync(user, "Hardware", "ManualBarrier", $"{barrier} barrier {(open ? "opened" : "closed")} manually", reason: reason, ct: ct).ConfigureAwait(false);
    }

    public async Task ManualLightsAsync(UserContext user, LightColor entry, LightColor exit, string reason, CancellationToken ct = default)
    {
        user.Demand(Permissions.HardwareOverride);
        if (_hardware is null) throw new InvalidOperationException("No I/O hardware is configured.");
        await _hardware.SetLightsAsync(entry, exit, ct).ConfigureAwait(false);
        await _audit.WriteAsync(user, "Hardware", "ManualLights", $"Lights set manually: entry {entry}, exit {exit}", reason: reason, ct: ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ loop

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), _time);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
                var now = _time.GetUtcNow();
                if (_hardware is not null && (now - _lastSensorPoll).TotalMilliseconds >= _inputPollMs)
                {
                    _lastSensorPoll = now;
                    _sensors = await _hardware.ReadSensorsAsync(ct).ConfigureAwait(false);
                }
                var inputs = new AutomationInputs(now, _indicator.Snapshot, _sensors?.Presence, _sensors?.PositionOk, _sensors?.SafetyOk ?? true);
                await Locked(() => _engine.Tick(inputs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log.LogError(e, "Automation loop error");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task OnIdentifiedAsync(VehicleIdentification id)
    {
        try
        {
            await Locked(() => _engine.OnIdentification(id, _time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogError(e, "Identification handling failed");
        }
    }

    /// <summary>Runs an engine call under the lock, then executes its commands (outside the lock).</summary>
    private async Task Locked(Func<IReadOnlyList<AutomationCommand>> call, CancellationToken ct)
    {
        IReadOnlyList<AutomationCommand> commands;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            commands = call();
        }
        finally
        {
            _gate.Release();
        }
        if (commands.Count == 0) return;
        foreach (var c in commands) await ExecuteAsync(c, ct).ConfigureAwait(false);
        StatusChanged?.Invoke(this, Status);
    }

    private async Task ExecuteAsync(AutomationCommand command, CancellationToken ct)
    {
        try
        {
            switch (command)
            {
                case SetLightsCommand or BarrierCommand or AlarmCommand when _hardware is not null:
                    await _hardware.ExecuteAsync(command, ct).ConfigureAwait(false);
                    break;
                case Core.Automation.CaptureRequest request:
                    _ = Task.Run(() => AutoCaptureAsync(request), CancellationToken.None);
                    break;
                case PrintRequest print:
                    PrintRequested?.Invoke(this, print);
                    break;
                case AutomationEvent e:
                    _lastEvent = e.Message;
                    if (e.Kind != AutomationEventKind.StateChanged || e.Severity != EventSeverity.Info)
                    {
                        await _audit.WriteAsync(_actor, "Automation", e.Kind.ToString(), e.Message, "AutomationSession", e.SessionId?.ToString("D"), ct: ct).ConfigureAwait(false);
                    }
                    if (e.Severity == EventSeverity.Alarm || e.Kind is AutomationEventKind.IncompleteWeighing or AutomationEventKind.ExitOverdue)
                    {
                        await _notifications.NotifyAsync(e.Severity == EventSeverity.Alarm ? "Error" : "Warning", "Automation", e.Kind.ToString(), e.Message, ct: ct).ConfigureAwait(false);
                    }
                    break;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or System.Data.Common.DbException)
        {
            _log.LogWarning("Automation command {Command} failed: {Message}", command.GetType().Name, e.Message);
            _lastEvent = $"{command.GetType().Name} failed: {e.Message}";
        }
    }

    private async Task AutoCaptureAsync(Core.Automation.CaptureRequest request)
    {
        CaptureResult result;
        try
        {
            var weighing = await _settings.GetAsync<WeighingSettings>(SettingKeys.Weighing).ConfigureAwait(false);
            var snapshot = _indicator.Snapshot;
            var frames = snapshot.Latest is { } r && _indicator.Current is { } svc ? svc.EvidenceFor(r.Sequence, weighing.Capture.EvidenceFrameCount) : [];
            var (images, _) = await _cameras.CaptureAllAsync(TimeSpan.FromMilliseconds(weighing.CameraCaptureTimeoutMs)).ConfigureAwait(false);
            var context = await _weighing.VehicleContextAsync(request.Vehicle?.Registration ?? string.Empty).ConfigureAwait(false);
            result = await _weighing.CaptureAsync(_actor, new Weighing.CaptureRequest
            {
                Details = new TransactionDetails { VehicleReg = request.Vehicle?.Registration ?? "UNKNOWN" },
                Mode = OperatingMode.Automatic,
                Snapshot = snapshot,
                EvidenceFrames = frames,
                Images = images,
                UseStoredTare = weighing.OfferStoredTare && context.Pending is null && context.StoredTareUsable,
                ConfirmDuplicate = false,
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogError(e, "Automatic capture failed");
            result = CaptureResult.Fail(CaptureFailure.Invalid, e.Message);
        }
        AutoCaptured?.Invoke(this, result);
        await Locked(() => _engine.OnCaptureCompleted(new CaptureOutcome(result.Success, result.Transaction?.Id, result.Measurement?.Weight.Kilograms, result.IsFinal, result.Success ? null : result.Message), _time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<AutomationOptions> OptionsAsync(OperatingMode mode, CancellationToken ct)
    {
        var automation = await _settings.GetAsync<AutomationSettings>(SettingKeys.Automation, ct).ConfigureAwait(false);
        var printing = await _settings.GetAsync<PrintingSettings>(SettingKeys.Printing, ct).ConfigureAwait(false);
        var limits = await _weighing.LimitsAsync(ct).ConfigureAwait(false);
        return automation.ToOptions(mode, limits, _hardware?.Config ?? new HardwareConfig(), printing);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
        }
        _gate.Dispose();
    }
}
