using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Protocols;

namespace Weighbridge.Devices.Simulation;

public enum SimPhase { Empty, Entering, Settling, OnDeck, Leaving }

public sealed record SimVehicle(string Registration, decimal TareKg, decimal LoadKg, string RfidTag);

public sealed record SimulatorReading(decimal Kg, bool Moving, bool Overload, SimPhase Phase);

/// <summary>
/// A virtual weighbridge yard: trucks drive on, settle with a damped oscillation, stay, and leave. The same world
/// feeds the simulated indicator, the simulated I/O (presence/position sensors, barriers) and the simulated
/// ANPR/RFID reader, so the complete automatic workflow can be exercised without hardware.
/// </summary>
public sealed partial class SimulatorWorld
{
    private readonly object _gate = new();
    private readonly SimulatorSettings _settings;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly List<SimVehicle> _fleet;
    private readonly Dictionary<string, bool> _loaded = new(StringComparer.Ordinal);
    private int _nextVehicle;
    private SimPhase _phase = SimPhase.Empty;
    private DateTimeOffset _phaseStart;
    private double _phaseLengthSeconds = 4;
    private decimal _targetKg;
    private SimVehicle? _vehicle;

    public SimulatorWorld(SimulatorSettings settings, TimeProvider? time = null, int seed = 20260101)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _random = new Random(seed);
        _phaseStart = _time.GetUtcNow();
        _fleet =
        [
            new("WP CAB-1234", settings.TruckTareKg, settings.TruckGrossKg - settings.TruckTareKg, "E2000017221101441890ABCD"),
            new("NC LK-5521", 9850m, 14200m, "E2000017221101441890BEEF"),
            new("SP KA-7788", 15400m, 26100m, "E2000017221101441890C0DE"),
            new("NW 62-0931", 7600m, 8400m, "E2000017221101441890D00D"),
        ];
    }

    public IReadOnlyList<SimVehicle> Fleet => _fleet;

    /// <summary>Raised (on the caller's thread of <see cref="Read"/>) when a vehicle starts driving onto the deck.</summary>
    public event Action<SimVehicle, bool>? VehicleArriving;

    /// <summary>Simulated exit barrier state (driven by the simulated I/O controller).</summary>
    public bool ExitBarrierOpen { get; set; }

    /// <summary>When true the truck stays on the deck until the exit barrier opens (realistic automatic mode).</summary>
    public bool WaitForExitBarrier { get; set; }

    public bool AutoCycle { get; set; }
    public bool SimulateDisconnect { get; set; }
    public bool ForceOverload { get; set; }
    public decimal ManualOffsetKg { get; set; }

    public SimPhase Phase { get { lock (_gate) return _phase; } }
    public SimVehicle? CurrentVehicle { get { lock (_gate) return _vehicle; } }
    public bool PresenceDetected => Phase != SimPhase.Empty;
    public bool PositionOk => Phase is SimPhase.Settling or SimPhase.OnDeck;

    public void DriveOn(SimVehicle vehicle, bool loaded)
    {
        lock (_gate) BeginEntering(vehicle, loaded, _time.GetUtcNow());
    }

    public void DriveOff()
    {
        lock (_gate)
        {
            if (_phase is SimPhase.Empty or SimPhase.Leaving) return;
            SetPhase(SimPhase.Leaving, _time.GetUtcNow(), 4);
        }
    }

    public SimulatorReading Read() => Read(_time.GetUtcNow());

    public SimulatorReading Read(DateTimeOffset now)
    {
        (SimVehicle Vehicle, bool Loaded)? arriving = null;
        SimulatorReading result;
        lock (_gate)
        {
            Advance(now, ref arriving);
            var t = (now - _phaseStart).TotalSeconds;
            var noise = (decimal)((_random.NextDouble() - 0.5) * (double)_settings.NoiseKg);
            decimal kg;
            bool moving;
            switch (_phase)
            {
                case SimPhase.Entering:
                    kg = _targetKg * (decimal)Smooth(t / _phaseLengthSeconds) + noise * 4;
                    moving = true;
                    break;
                case SimPhase.Settling:
                    var amplitude = (double)_targetKg * 0.03 * Math.Exp(-t / 0.7);
                    kg = _targetKg + (decimal)(amplitude * Math.Sin(2 * Math.PI * 1.4 * t)) + noise;
                    moving = Math.Abs(amplitude) > (double)_settings.DivisionKg;
                    break;
                case SimPhase.OnDeck:
                    kg = _targetKg + noise;
                    moving = false;
                    break;
                case SimPhase.Leaving:
                    kg = _targetKg * (decimal)(1 - Smooth(t / _phaseLengthSeconds)) + noise * 4;
                    moving = true;
                    break;
                default:
                    kg = noise / 4;
                    moving = false;
                    break;
            }
            kg += _phase == SimPhase.Empty ? 0 : ManualOffsetKg;
            var division = _settings.DivisionKg <= 0 ? 1 : _settings.DivisionKg;
            kg = Math.Round(kg / division, MidpointRounding.AwayFromZero) * division;
            var overload = ForceOverload || kg > _settings.CapacityKg;
            result = new SimulatorReading(kg, moving, overload, _phase);
        }
        if (arriving is { } a) VehicleArriving?.Invoke(a.Vehicle, a.Loaded);
        return result;
    }

    private void Advance(DateTimeOffset now, ref (SimVehicle, bool)? arriving)
    {
        var elapsed = (now - _phaseStart).TotalSeconds;
        switch (_phase)
        {
            case SimPhase.Empty when AutoCycle && elapsed >= _phaseLengthSeconds:
                var v = _fleet[_nextVehicle];
                _nextVehicle = (_nextVehicle + 1) % _fleet.Count;
                var loaded = !_loaded.GetValueOrDefault(v.Registration);
                BeginEntering(v, loaded, now);
                arriving = (v, loaded);
                break;
            case SimPhase.Entering when elapsed >= _phaseLengthSeconds:
                SetPhase(SimPhase.Settling, now, 3);
                break;
            case SimPhase.Settling when elapsed >= _phaseLengthSeconds:
                SetPhase(SimPhase.OnDeck, now, 8 + _random.NextDouble() * 4);
                break;
            case SimPhase.OnDeck when AutoCycle && (WaitForExitBarrier ? ExitBarrierOpen && elapsed >= 1 : elapsed >= _phaseLengthSeconds):
                SetPhase(SimPhase.Leaving, now, 4);
                break;
            case SimPhase.Leaving when elapsed >= _phaseLengthSeconds:
                if (_vehicle is { } left) _loaded[left.Registration] = !_loaded.GetValueOrDefault(left.Registration);
                _vehicle = null;
                SetPhase(SimPhase.Empty, now, 6 + _random.NextDouble() * 4);
                break;
        }
    }

    private void BeginEntering(SimVehicle vehicle, bool loaded, DateTimeOffset now)
    {
        _vehicle = vehicle;
        _loaded[vehicle.Registration] = !loaded; // flips back to "loaded" when it leaves
        _targetKg = vehicle.TareKg + (loaded ? vehicle.LoadKg : 0);
        SetPhase(SimPhase.Entering, now, 4);
    }

    private void SetPhase(SimPhase phase, DateTimeOffset now, double seconds)
    {
        _phase = phase;
        _phaseStart = now;
        _phaseLengthSeconds = seconds;
    }

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    // ------------------------------------------------------------------ output formatting

    [GeneratedRegex(@"\{(weight|status|mode|unit|sign)(?::([^}]+))?\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Formats a reading using a profile-notation template, e.g. <c>"{status},{mode},{weight:+000000.0}kg\r\n"</c>,
    /// or the special templates <c>@toledo</c> / <c>@toledo+cs</c> (binary Mettler Toledo continuous output).
    /// </summary>
    public static byte[] Format(string template, SimulatorReading r)
    {
        if (template.StartsWith("@toledo", StringComparison.OrdinalIgnoreCase))
        {
            return ToledoContinuousProtocol.Encode(r.Kg, r.Moving, withChecksum: template.EndsWith("+cs", StringComparison.OrdinalIgnoreCase), overload: r.Overload);
        }

        var text = Placeholder().Replace(template, m => m.Groups[1].Value switch
        {
            "weight" when r.Overload => "OL",
            "weight" => FormatWeight(r.Kg, m.Groups[2].Success ? m.Groups[2].Value : "0"),
            "status" => r.Overload ? "OL" : r.Moving ? "US" : "ST",
            "mode" => "GS",
            "unit" => "kg",
            "sign" => r.Kg < 0 ? "-" : "+",
            _ => m.Value,
        });
        return ByteText.Unescape(text);
    }

    private static string FormatWeight(decimal kg, string format)
    {
        if (format.StartsWith('+'))
        {
            return (kg < 0 ? "-" : "+") + Math.Abs(kg).ToString(format[1..], CultureInfo.InvariantCulture);
        }
        return kg.ToString(format, CultureInfo.InvariantCulture);
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            sb.Append(_phase);
            if (_vehicle is { } v) sb.Append(' ').Append(v.Registration).Append(" target ").Append(_targetKg.ToString("0", CultureInfo.InvariantCulture)).Append(" kg");
        }
        return sb.ToString();
    }
}
