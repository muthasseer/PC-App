using System.IO.Ports;
using Weighbridge.Core.Indicators;

namespace Weighbridge.Devices.Transports;

/// <summary>RS-232 / RS-485 / USB-serial / virtual COM port via System.IO.Ports.</summary>
public sealed class SerialByteTransport(SerialSettings settings) : IByteTransport
{
    private SerialPort? _port;
    private int _uartErrors;

    public string Description => settings.Summary;
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>Framing/parity/overrun errors reported by the UART since the last call (used by the baud scanner).</summary>
    public int TakeUartErrors() => Interlocked.Exchange(ref _uartErrors, 0);

    public void Open()
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate, Map(settings.Parity), settings.DataBits, Map(settings.StopBits))
        {
            Handshake = Map(settings.Handshake),
            ReadBufferSize = Math.Max(1024, settings.ReadBufferSize),
            ReadTimeout = 100,
            WriteTimeout = 1000,
            Encoding = System.Text.Encoding.Latin1,
            DiscardNull = false,
        };
        port.ErrorReceived += (_, _) => Interlocked.Increment(ref _uartErrors);
        port.Open();
        // Many indicators take power or "data request" from DTR/RTS. Some virtual ports (and pseudo-terminals)
        // do not support modem-control lines; that is not fatal.
        TrySet(() => port.DtrEnable = settings.DtrEnable);
        if (settings.Handshake is HandshakeKind.None or HandshakeKind.XOnXOff) TrySet(() => port.RtsEnable = settings.RtsEnable);
        TrySet(port.DiscardInBuffer);
        _port = port;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var port = _port ?? throw new InvalidOperationException("The port is not open.");
        if (!port.IsOpen) throw new IOException($"{settings.PortName} was closed.");
        port.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            var available = port.BytesToRead;
            if (available == 0)
            {
                // Block for the first byte (honours ReadTimeout), then drain whatever else is buffered.
                var first = port.ReadByte();
                if (first < 0) throw new IOException($"{settings.PortName} reached end of stream.");
                buffer[0] = (byte)first;
                var more = Math.Min(port.BytesToRead, buffer.Length - 1);
                if (more <= 0) return 1;
                var tmp = new byte[more];
                var n = port.Read(tmp, 0, more);
                tmp.AsSpan(0, n).CopyTo(buffer[1..]);
                return 1 + n;
            }
            var count = Math.Min(available, buffer.Length);
            var chunk = new byte[count];
            var read = port.Read(chunk, 0, count);
            chunk.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (InvalidOperationException e)
        {
            // Thrown when a USB adapter is unplugged while open.
            throw new IOException($"{settings.PortName} is no longer available: {e.Message}", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException($"{settings.PortName} access failed: {e.Message}", e);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var port = _port ?? throw new InvalidOperationException("The port is not open.");
        port.Write(data.ToArray(), 0, data.Length);
    }

    public void Dispose()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is null) return;
        try
        {
            if (port.IsOpen) port.Close();
        }
        catch (IOException)
        {
            // Unplugged adapters can throw on close; the handle is released by Dispose.
        }
        port.Dispose();
    }

    private static void TrySet(Action action)
    {
        try { action(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static Parity Map(ParityKind p) => p switch
    {
        ParityKind.Odd => Parity.Odd,
        ParityKind.Even => Parity.Even,
        ParityKind.Mark => Parity.Mark,
        ParityKind.Space => Parity.Space,
        _ => Parity.None,
    };

    public static StopBits Map(StopBitsKind s) => s switch
    {
        StopBitsKind.Two => StopBits.Two,
        StopBitsKind.OnePointFive => StopBits.OnePointFive,
        _ => StopBits.One,
    };

    public static Handshake Map(HandshakeKind h) => h switch
    {
        HandshakeKind.XOnXOff => Handshake.XOnXOff,
        HandshakeKind.RequestToSend => Handshake.RequestToSend,
        HandshakeKind.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => Handshake.None,
    };
}
