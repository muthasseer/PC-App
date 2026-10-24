using Weighbridge.Core.Indicators;

namespace Weighbridge.Devices.Transports;

/// <summary>
/// A blocking byte pipe used from a dedicated background thread. Blocking reads with a short timeout keep the
/// receive loop responsive to shutdown on every platform (serial-port async reads cannot be cancelled reliably).
/// </summary>
public interface IByteTransport : IDisposable
{
    string Description { get; }
    bool IsOpen { get; }

    void Open();

    /// <summary>Returns the number of bytes read, or 0 when nothing arrived within <paramref name="timeoutMs"/>.
    /// Throws <see cref="IOException"/> when the connection is lost.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    void Write(ReadOnlySpan<byte> data);
}

public interface ITransportFactory
{
    IByteTransport Create(ConnectionSettings settings);
}

public sealed class TransportFactory(Simulation.SimulatorWorld? simulatorWorld = null, TimeProvider? time = null) : ITransportFactory
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IByteTransport Create(ConnectionSettings settings) => settings.Transport switch
    {
        TransportKind.Serial => new SerialByteTransport(settings.Serial),
        TransportKind.TcpClient => new TcpByteTransport(settings.Tcp),
        TransportKind.Simulator => new SimulatorTransport(simulatorWorld ?? new Simulation.SimulatorWorld(settings.Simulator, _time), settings.Simulator, _time),
        TransportKind.Replay => new ReplayTransport(settings.ReplayFile ?? throw new InvalidOperationException("No replay file configured."), loop: true),
        _ => throw new NotSupportedException($"Transport {settings.Transport} is not supported."),
    };
}
