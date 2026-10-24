using System.Net.Sockets;
using Weighbridge.Core.Indicators;

namespace Weighbridge.Devices.Transports;

/// <summary>Raw TCP client for serial-to-Ethernet servers (Moxa, USR, Lantronix…) and indicators with a TCP port.</summary>
public sealed class TcpByteTransport(TcpSettings settings) : IByteTransport
{
    private Socket? _socket;

    public string Description => $"tcp://{settings.Host}:{settings.Port}";
    public bool IsOpen => _socket?.Connected == true;

    public void Open()
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            socket.ConnectAsync(settings.Host, settings.Port, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw new IOException($"Connection to {Description} timed out.");
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new IOException($"Cannot connect to {Description}: {e.Message}", e);
        }
        _socket = socket;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected.");
        try
        {
            if (!socket.Poll(Math.Max(1, timeoutMs) * 1000, SelectMode.SelectRead)) return 0;
            var n = socket.Receive(buffer);
            if (n == 0) throw new IOException($"{Description} closed the connection.");
            return n;
        }
        catch (SocketException e)
        {
            throw new IOException($"{Description}: {e.Message}", e);
        }
        catch (ObjectDisposedException e)
        {
            throw new IOException($"{Description} was closed.", e);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected.");
        try
        {
            socket.Send(data);
        }
        catch (SocketException e)
        {
            throw new IOException($"{Description}: {e.Message}", e);
        }
    }

    public void Dispose()
    {
        var s = Interlocked.Exchange(ref _socket, null);
        if (s is null) return;
        try { s.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { }
        s.Dispose();
    }
}
