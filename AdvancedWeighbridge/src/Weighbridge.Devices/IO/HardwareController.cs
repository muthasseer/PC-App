using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weighbridge.Core.Automation;
using Weighbridge.Devices.Cameras;
using Weighbridge.Devices.Identification;
using Weighbridge.Devices.Simulation;

namespace Weighbridge.Devices.IO;

public enum OutputSignal
{
    EntryRed, EntryAmber, EntryGreen,
    ExitRed, ExitAmber, ExitGreen,
    EntryBarrierOpen, EntryBarrierClose,
    ExitBarrierOpen, ExitBarrierClose,
    Alarm,
}

public enum InputSignal
{
    /// <summary>Vehicle presence (loop detector or photo-cell covering the deck).</summary>
    Presence,

    /// <summary>Beam at the entry edge of the platform is interrupted (vehicle not fully on).</summary>
    EntryBeamBlocked,

    /// <summary>Beam at the exit edge of the platform is interrupted.</summary>
    ExitBeamBlocked,

    /// <summary>Safety chain healthy (emergency stop released, barrier zone clear). Fail-safe: unreadable = not OK.</summary>
    SafetyOk,
}

public sealed record SignalBinding(string ControllerId, int Channel, bool Invert = false);

public enum BarrierDriveMode
{
    /// <summary>The "open" output stays on while the barrier should be open.</summary>
    Level = 0,

    /// <summary>A short pulse on the open output opens, a pulse on the close output closes.</summary>
    Pulse = 1,
}

public sealed record HardwareConfig
{
    public List<IoControllerConfig> Controllers { get; init; } = [];
    public Dictionary<OutputSignal, SignalBinding> Outputs { get; init; } = [];
    public Dictionary<InputSignal, SignalBinding> Inputs { get; init; } = [];
    public BarrierDriveMode BarrierMode { get; init; } = BarrierDriveMode.Level;
    public int PulseMs { get; init; } = 600;
    public int InputPollMs { get; init; } = 200;
    public List<CameraConfig> Cameras { get; init; } = [];
    public List<IdentificationSourceConfig> Identification { get; init; } = [];

    public bool HasLights => Outputs.ContainsKey(OutputSignal.EntryRed) || Outputs.ContainsKey(OutputSignal.ExitRed);
    public bool HasExitBarrier => Outputs.ContainsKey(OutputSignal.ExitBarrierOpen);
    public bool HasPositionSensors => Inputs.ContainsKey(InputSignal.EntryBeamBlocked) || Inputs.ContainsKey(InputSignal.ExitBeamBlocked);
    public bool HasPresenceSensor => Inputs.ContainsKey(InputSignal.Presence);

    /// <summary>Everything wired to the simulator: lights, barriers, alarm, presence, beams and safety.</summary>
    public static HardwareConfig CreateSimulated() => new()
    {
        Controllers = [new IoControllerConfig { Id = "sim", Name = "Simulated I/O", Kind = IoControllerKind.Simulated }],
        Outputs = Enum.GetValues<OutputSignal>().ToDictionary(s => s, s => new SignalBinding("sim", (int)s)),
        Inputs = new Dictionary<InputSignal, SignalBinding>
        {
            [InputSignal.Presence] = new("sim", 0),
            [InputSignal.EntryBeamBlocked] = new("sim", 1),
            [InputSignal.ExitBeamBlocked] = new("sim", 2),
            [InputSignal.SafetyOk] = new("sim", 3),
        },
        Identification = [new IdentificationSourceConfig { Id = "sim-anpr", Name = "Simulated ANPR", Kind = IdentificationKind.Simulated }],
    };
}

public sealed record SensorState(bool? Presence, bool? PositionOk, bool SafetyOk, DateTimeOffset At, string? Error);

public sealed record HardwareStatus(
    LightColor EntryLight,
    LightColor ExitLight,
    bool? EntryBarrierOpen,
    bool? ExitBarrierOpen,
    bool AlarmOn,
    SensorState? Sensors,
    string? LastError);

/// <summary>Maps automation commands to physical outputs and reads sensors into automation inputs.</summary>
public sealed class HardwareController : IDisposable
{
    private readonly HardwareConfig _config;
    private readonly Dictionary<string, IIoController> _controllers;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private LightColor _entry = LightColor.Off;
    private LightColor _exit = LightColor.Off;
    private bool? _entryBarrier;
    private bool? _exitBarrier;
    private bool _alarm;
    private SensorState? _sensors;
    private string? _lastError;

    public HardwareController(HardwareConfig config, Func<IoControllerConfig, IIoController> factory, ILogger? log = null, TimeProvider? time = null)
    {
        _config = config;
        _controllers = config.Controllers.ToDictionary(c => c.Id, factory, StringComparer.OrdinalIgnoreCase);
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public HardwareConfig Config => _config;

    public HardwareStatus Status => new(_entry, _exit, _entryBarrier, _exitBarrier, _alarm, _sensors, _lastError);

    public static Func<IoControllerConfig, IIoController> DefaultFactory(SimulatorWorld? world, HttpClient? http = null) => c => c.Kind switch
    {
        IoControllerKind.ModbusTcp => new ModbusIoController(c, new ModbusTcpClient(c.Host, c.Port, c.UnitId, c.TimeoutMs)),
        IoControllerKind.ModbusRtu => new ModbusIoController(c, new ModbusRtuClient(c.Serial, c.UnitId, c.TimeoutMs)),
        IoControllerKind.HttpRelay => new HttpRelayIoController(c, http ?? new HttpClient()),
        _ => new SimulatedIoController(c.Id, world),
    };

    public async Task ExecuteAsync(AutomationCommand command, CancellationToken ct = default)
    {
        switch (command)
        {
            case SetLightsCommand l:
                await SetLightsAsync(l.Entry, l.Exit, ct).ConfigureAwait(false);
                break;
            case BarrierCommand b:
                await SetBarrierAsync(b.Barrier, b.Open, ct).ConfigureAwait(false);
                break;
            case AlarmCommand a:
                await SetAlarmAsync(a.On, ct).ConfigureAwait(false);
                break;
        }
    }

    public async Task SetLightsAsync(LightColor entry, LightColor exit, CancellationToken ct = default)
    {
        await WriteLight(OutputSignal.EntryRed, OutputSignal.EntryAmber, OutputSignal.EntryGreen, entry, ct).ConfigureAwait(false);
        await WriteLight(OutputSignal.ExitRed, OutputSignal.ExitAmber, OutputSignal.ExitGreen, exit, ct).ConfigureAwait(false);
        _entry = entry;
        _exit = exit;
    }

    public async Task SetBarrierAsync(BarrierId barrier, bool open, CancellationToken ct = default)
    {
        var (openSignal, closeSignal) = barrier == BarrierId.Exit
            ? (OutputSignal.ExitBarrierOpen, OutputSignal.ExitBarrierClose)
            : (OutputSignal.EntryBarrierOpen, OutputSignal.EntryBarrierClose);
        if (!_config.Outputs.ContainsKey(openSignal)) return;

        if (_config.BarrierMode == BarrierDriveMode.Level)
        {
            await Write(openSignal, open, ct).ConfigureAwait(false);
        }
        else
        {
            var signal = open ? openSignal : closeSignal;
            await Write(signal, true, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, _config.PulseMs)), _time, ct).ConfigureAwait(false);
            await Write(signal, false, ct).ConfigureAwait(false);
        }
        if (barrier == BarrierId.Exit) _exitBarrier = open;
        else _entryBarrier = open;
    }

    public async Task SetAlarmAsync(bool on, CancellationToken ct = default)
    {
        await Write(OutputSignal.Alarm, on, ct).ConfigureAwait(false);
        _alarm = on;
    }

    public async Task<SensorState> ReadSensorsAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        try
        {
            var presence = await ReadAsync(InputSignal.Presence, ct).ConfigureAwait(false);
            var entryBeam = await ReadAsync(InputSignal.EntryBeamBlocked, ct).ConfigureAwait(false);
            var exitBeam = await ReadAsync(InputSignal.ExitBeamBlocked, ct).ConfigureAwait(false);
            var safety = await ReadAsync(InputSignal.SafetyOk, ct).ConfigureAwait(false);
            bool? positionOk = entryBeam is null && exitBeam is null ? null : !(entryBeam == true || exitBeam == true);
            _sensors = new SensorState(presence, positionOk, safety ?? true, now, null);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or NotSupportedException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            _lastError = e.Message;
            _log.LogWarning("Sensor read failed: {Message}", e.Message);
            // Fail safe: unknown safety state is treated as not OK so that no barrier opens.
            _sensors = new SensorState(null, null, SafetyOk: false, now, e.Message);
        }
        return _sensors;
    }

    private async Task WriteLight(OutputSignal red, OutputSignal amber, OutputSignal green, LightColor color, CancellationToken ct)
    {
        await Write(red, color == LightColor.Red, ct).ConfigureAwait(false);
        await Write(amber, color == LightColor.Amber, ct).ConfigureAwait(false);
        await Write(green, color == LightColor.Green, ct).ConfigureAwait(false);
    }

    private async Task Write(OutputSignal signal, bool on, CancellationToken ct)
    {
        if (!_config.Outputs.TryGetValue(signal, out var binding)) return;
        if (!_controllers.TryGetValue(binding.ControllerId, out var controller)) throw new InvalidOperationException($"Unknown I/O controller '{binding.ControllerId}'.");
        try
        {
            await controller.WriteOutputAsync(binding.Channel, on ^ binding.Invert, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _lastError = $"{signal}: {e.Message}";
            _log.LogWarning("Output {Signal} failed: {Message}", signal, e.Message);
            throw;
        }
    }

    private async Task<bool?> ReadAsync(InputSignal signal, CancellationToken ct)
    {
        if (!_config.Inputs.TryGetValue(signal, out var binding)) return null;
        if (!_controllers.TryGetValue(binding.ControllerId, out var controller)) throw new InvalidOperationException($"Unknown I/O controller '{binding.ControllerId}'.");
        var values = await controller.ReadInputsAsync(binding.Channel, 1, ct).ConfigureAwait(false);
        return values[0] ^ binding.Invert;
    }

    public void Dispose()
    {
        foreach (var c in _controllers.Values) c.Dispose();
    }
}
