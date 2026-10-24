using Weighbridge.Core.Indicators;
using Weighbridge.Devices.Simulation;

namespace Weighbridge.Devices.Transports;

/// <summary>A virtual indicator that streams frames generated from a <see cref="SimulatorWorld"/>.</summary>
public sealed class SimulatorTransport(SimulatorWorld world, SimulatorSettings settings, TimeProvider time) : IByteTransport
{
    private byte[] _pending = [];
    private int _pendingOffset;
    private DateTimeOffset _nextFrameAt;
    private bool _open;

    public SimulatorWorld World => world;
    public string Description => "Simulator";
    public bool IsOpen => _open;

    public void Open()
    {
        if (world.SimulateDisconnect) throw new IOException("Simulated indicator is disconnected.");
        world.AutoCycle = settings.AutoCycle || world.AutoCycle;
        _open = true;
        _nextFrameAt = time.GetUtcNow();
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (!_open) throw new InvalidOperationException("Not open.");
        if (world.SimulateDisconnect)
        {
            _open = false;
            throw new IOException("Simulated cable unplugged.");
        }

        if (_pendingOffset >= _pending.Length)
        {
            var now = time.GetUtcNow();
            var wait = _nextFrameAt - now;
            if (wait > TimeSpan.Zero)
            {
                var sleep = TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds, timeoutMs));
                Thread.Sleep(sleep);
                if (time.GetUtcNow() < _nextFrameAt) return 0;
            }
            _nextFrameAt = time.GetUtcNow().AddMilliseconds(Math.Max(10, settings.IntervalMs));
            _pending = SimulatorWorld.Format(settings.Template, world.Read());
            _pendingOffset = 0;
        }

        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer);
        _pendingOffset += n;
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        // A poll request makes the next frame due immediately.
        _nextFrameAt = time.GetUtcNow();
    }

    public void Dispose() => _open = false;
}
