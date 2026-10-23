using System.Collections.Concurrent;
using System.Globalization;
using Weighbridge.Core.Indicators;
using Weighbridge.Devices.Simulation;

namespace Weighbridge.Devices.IO;

public enum IoControllerKind
{
    Simulated = 0,
    ModbusTcp = 1,
    ModbusRtu = 2,
    HttpRelay = 3,
}

public sealed record IoControllerConfig
{
    public string Id { get; init; } = "io1";
    public string Name { get; init; } = "I/O module";
    public IoControllerKind Kind { get; init; } = IoControllerKind.Simulated;
    public string Host { get; init; } = "192.168.1.200";
    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;
    public SerialSettings Serial { get; init; } = new() { PortName = "COM5", BaudRate = 9600 };
    public int TimeoutMs { get; init; } = 1000;

    /// <summary>Read inputs with function 01 (coils) instead of 02 (discrete inputs) – some modules map inputs that way.</summary>
    public bool InputsAreCoils { get; init; }

    /// <summary>HTTP relay boards: URL templates with {channel}, e.g. <c>http://192.168.1.50/relay/{channel}/on</c>.</summary>
    public string? HttpOnUrl { get; init; }
    public string? HttpOffUrl { get; init; }

    /// <summary>Optional URL returning the input states as a string of 0/1 characters.</summary>
    public string? HttpInputsUrl { get; init; }
}

public interface IIoController : IDisposable
{
    string Id { get; }
    Task WriteOutputAsync(int channel, bool on, CancellationToken ct);
    Task<IReadOnlyList<bool>> ReadInputsAsync(int first, int count, CancellationToken ct);
}

public sealed class ModbusIoController(IoControllerConfig config, IModbusClient client) : IIoController
{
    public string Id => config.Id;

    public Task WriteOutputAsync(int channel, bool on, CancellationToken ct) => client.WriteCoilAsync((ushort)channel, on, ct);

    public async Task<IReadOnlyList<bool>> ReadInputsAsync(int first, int count, CancellationToken ct) =>
        await client.ReadAsync(config.InputsAreCoils ? ModbusPdu.ReadCoils : ModbusPdu.ReadDiscreteInputs, (ushort)first, (ushort)count, ct).ConfigureAwait(false);

    public void Dispose() => client.Dispose();
}

/// <summary>Relay boards controlled by HTTP GET requests (common low-cost Ethernet/Wi-Fi relays).</summary>
public sealed class HttpRelayIoController(IoControllerConfig config, HttpClient http) : IIoController
{
    public string Id => config.Id;

    public async Task WriteOutputAsync(int channel, bool on, CancellationToken ct)
    {
        var template = on ? config.HttpOnUrl : config.HttpOffUrl;
        if (string.IsNullOrWhiteSpace(template)) throw new InvalidOperationException($"No HTTP URL configured for relay {(on ? "on" : "off")}.");
        var url = template.Replace("{channel}", channel.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);
        using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<bool>> ReadInputsAsync(int first, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.HttpInputsUrl)) throw new NotSupportedException("This relay board has no input URL configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);
        var text = await http.GetStringAsync(config.HttpInputsUrl, timeout.Token).ConfigureAwait(false);
        var bits = text.Where(c => c is '0' or '1').Select(c => c == '1').ToList();
        if (bits.Count < first + count) throw new IOException($"Input URL returned {bits.Count} inputs, {first + count} needed.");
        return bits.Skip(first).Take(count).ToList();
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// In-memory I/O used with the simulator: input 0 = vehicle presence, 1 = entry beam blocked, 2 = exit beam blocked,
/// 3 = safety OK. Writing <see cref="ExitBarrierChannel"/> moves the simulated exit barrier.
/// </summary>
public sealed class SimulatedIoController(string id, SimulatorWorld? world = null) : IIoController
{
    public const int ExitBarrierChannel = (int)OutputSignal.ExitBarrierOpen;
    private readonly ConcurrentDictionary<int, bool> _outputs = new();

    public string Id => id;
    public IReadOnlyDictionary<int, bool> Outputs => _outputs;
    public bool SafetyOk { get; set; } = true;

    public Task WriteOutputAsync(int channel, bool on, CancellationToken ct)
    {
        _outputs[channel] = on;
        if (world is not null && channel == ExitBarrierChannel) world.ExitBarrierOpen = on;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<bool>> ReadInputsAsync(int first, int count, CancellationToken ct)
    {
        var all = new[]
        {
            world?.PresenceDetected ?? false,
            world is not null && world.Phase is SimPhase.Entering or SimPhase.Leaving,
            false,
            SafetyOk,
        };
        var result = new List<bool>(count);
        for (var i = first; i < first + count; i++) result.Add(i < all.Length && all[i]);
        return Task.FromResult<IReadOnlyList<bool>>(result);
    }

    public void Dispose()
    {
    }
}
