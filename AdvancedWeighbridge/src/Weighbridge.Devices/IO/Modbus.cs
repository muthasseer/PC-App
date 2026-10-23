using System.Buffers.Binary;
using System.IO.Ports;
using System.Net.Sockets;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.IO;

public sealed class ModbusException(byte function, byte code)
    : IOException($"Modbus exception {code} ({Describe(code)}) for function {function}.")
{
    public byte ExceptionCode { get; } = code;

    private static string Describe(byte code) => code switch
    {
        1 => "illegal function",
        2 => "illegal data address",
        3 => "illegal data value",
        4 => "slave device failure",
        6 => "slave device busy",
        _ => "unknown",
    };
}

/// <summary>Modbus PDU encoding shared by TCP and RTU (functions 01, 02, 05).</summary>
public static class ModbusPdu
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte WriteSingleCoil = 0x05;

    public static byte[] ReadBits(byte function, ushort start, ushort count)
    {
        if (count is 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(count));
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        return pdu;
    }

    public static byte[] WriteCoil(ushort address, bool on)
    {
        var pdu = new byte[5];
        pdu[0] = WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), on ? (ushort)0xFF00 : (ushort)0x0000);
        return pdu;
    }

    public static void ThrowIfException(byte requestFunction, ReadOnlySpan<byte> response)
    {
        if (response.Length < 1) throw new IOException("Empty Modbus response.");
        if (response[0] == (byte)(requestFunction | 0x80)) throw new ModbusException(requestFunction, response.Length > 1 ? response[1] : (byte)0);
        if (response[0] != requestFunction) throw new IOException($"Unexpected Modbus function {response[0]} (expected {requestFunction}).");
    }

    public static bool[] ParseBits(byte function, ReadOnlySpan<byte> response, int count)
    {
        ThrowIfException(function, response);
        if (response.Length < 2) throw new IOException("Truncated Modbus response.");
        var byteCount = response[1];
        if (response.Length < 2 + byteCount || byteCount < (count + 7) / 8) throw new IOException("Truncated Modbus response.");
        var bits = new bool[count];
        for (var i = 0; i < count; i++) bits[i] = (response[2 + i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    /// <summary>Expected RTU response length for a request (unit + PDU + CRC), used to know when a reply is complete.</summary>
    public static int RtuResponseLength(byte function, ushort count) => function switch
    {
        ReadCoils or ReadDiscreteInputs => 1 + 2 + (count + 7) / 8 + 2,
        WriteSingleCoil => 8,
        _ => throw new NotSupportedException(),
    };
}

public interface IModbusClient : IDisposable
{
    Task<bool[]> ReadAsync(byte function, ushort start, ushort count, CancellationToken ct);
    Task WriteCoilAsync(ushort address, bool on, CancellationToken ct);
}

/// <summary>Modbus TCP client (port 502) for Ethernet relay/I-O modules and PLCs.</summary>
public sealed class ModbusTcpClient(string host, int port, byte unitId, int timeoutMs = 1000) : IModbusClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private ushort _transaction;

    public async Task<bool[]> ReadAsync(byte function, ushort start, ushort count, CancellationToken ct)
    {
        var response = await TransactAsync(ModbusPdu.ReadBits(function, start, count), ct).ConfigureAwait(false);
        return ModbusPdu.ParseBits(function, response, count);
    }

    public async Task WriteCoilAsync(ushort address, bool on, CancellationToken ct)
    {
        var request = ModbusPdu.WriteCoil(address, on);
        var response = await TransactAsync(request, ct).ConfigureAwait(false);
        ModbusPdu.ThrowIfException(ModbusPdu.WriteSingleCoil, response);
    }

    private async Task<byte[]> TransactAsync(byte[] pdu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                if (_client is not { Connected: true })
                {
                    _client?.Dispose();
                    _client = new TcpClient { NoDelay = true };
                    await _client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                }
                var stream = _client.GetStream();
                var id = ++_transaction;
                var adu = new byte[7 + pdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(0), id);
                BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(pdu.Length + 1));
                adu[6] = unitId;
                pdu.CopyTo(adu, 7);
                await stream.WriteAsync(adu, timeout.Token).ConfigureAwait(false);

                var header = new byte[7];
                await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt16BigEndian(header) != id) throw new IOException("Modbus transaction id mismatch.");
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (length is < 2 or > 260) throw new IOException($"Invalid Modbus length {length}.");
                var body = new byte[length - 1];
                await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                return body;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or EndOfStreamException or IOException && e is not ModbusException)
            {
                _client?.Dispose();
                _client = null;
                if (ct.IsCancellationRequested) throw;
                throw new IOException($"Modbus TCP {host}:{port}: {e.Message}", e);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Modbus RTU over RS-485/RS-232 (relay boards, small PLCs).</summary>
public sealed class ModbusRtuClient(SerialSettings settings, byte unitId, int timeoutMs = 1000) : IModbusClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialPort? _port;

    public Task<bool[]> ReadAsync(byte function, ushort start, ushort count, CancellationToken ct) =>
        Locked(() =>
        {
            var response = Transact(ModbusPdu.ReadBits(function, start, count), ModbusPdu.RtuResponseLength(function, count));
            return ModbusPdu.ParseBits(function, response, count);
        }, ct);

    public Task WriteCoilAsync(ushort address, bool on, CancellationToken ct) =>
        Locked(() =>
        {
            var response = Transact(ModbusPdu.WriteCoil(address, on), ModbusPdu.RtuResponseLength(ModbusPdu.WriteSingleCoil, 1));
            ModbusPdu.ThrowIfException(ModbusPdu.WriteSingleCoil, response);
            return true;
        }, ct);

    private async Task<T> Locked<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(action, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private byte[] Transact(byte[] pdu, int expectedLength)
    {
        if (_port is not { IsOpen: true })
        {
            _port?.Dispose();
            _port = new SerialPort(settings.PortName, settings.BaudRate, SerialByteTransport.Map(settings.Parity), settings.DataBits, SerialByteTransport.Map(settings.StopBits))
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs,
            };
            _port.Open();
        }
        var frame = new byte[1 + pdu.Length + 2];
        frame[0] = unitId;
        pdu.CopyTo(frame, 1);
        var crc = ChecksumValidator.Crc16Modbus(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        _port.DiscardInBuffer();
        _port.Write(frame, 0, frame.Length);

        var response = new List<byte>(expectedLength);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (response.Count < expectedLength && Environment.TickCount64 < deadline)
        {
            try
            {
                response.Add((byte)_port.ReadByte());
            }
            catch (TimeoutException)
            {
                break;
            }
            // An exception reply is shorter (unit, fc|0x80, code, crc).
            if (response.Count == 5 && (response[1] & 0x80) != 0) break;
        }
        if (response.Count < 5) throw new IOException($"Modbus RTU unit {unitId} did not answer on {settings.PortName}.");
        var data = response.ToArray();
        var expectedCrc = ChecksumValidator.Crc16Modbus(data.AsSpan(0, data.Length - 2));
        if (data[^2] != (byte)(expectedCrc & 0xFF) || data[^1] != (byte)(expectedCrc >> 8)) throw new IOException("Modbus RTU CRC error.");
        if (data[0] != unitId) throw new IOException($"Modbus RTU reply from unit {data[0]}, expected {unitId}.");
        return data[1..^2];
    }

    public void Dispose()
    {
        _port?.Dispose();
        _gate.Dispose();
    }
}
