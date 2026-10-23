using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Weighbridge.Core.Indicators;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Tests;

/// <summary>Scripted transport: hands out queued chunks, can simulate disconnects.</summary>
public sealed class FakeTransport : IByteTransport
{
    private readonly ConcurrentQueue<object> _script = new();
    public List<byte[]> Written { get; } = [];
    public int OpenCount { get; private set; }
    public bool FailOpen { get; set; }
    public string Description => "fake";
    public bool IsOpen { get; private set; }

    public FakeTransport Enqueue(string text) { _script.Enqueue(Encoding.Latin1.GetBytes(text)); return this; }
    public FakeTransport Enqueue(byte[] data) { _script.Enqueue(data); return this; }
    public FakeTransport Disconnect() { _script.Enqueue(new IOException("cable pulled")); return this; }

    public void Open()
    {
        OpenCount++;
        if (FailOpen) throw new IOException("port busy");
        IsOpen = true;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_script.TryDequeue(out var item))
        {
            if (item is Exception e)
            {
                IsOpen = false;
                throw e;
            }
            var data = (byte[])item;
            data.CopyTo(buffer);
            return data.Length;
        }
        Thread.Sleep(Math.Min(timeoutMs, 10));
        return 0;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (Written) Written.Add(data.ToArray());
    }

    public void Dispose() => IsOpen = false;
}

public sealed class FakeFactory(params FakeTransport[] transports) : ITransportFactory
{
    private int _next;
    public IByteTransport Create(ConnectionSettings settings) => transports[Math.Min(_next++, transports.Length - 1)];
}

public static class Wait
{
    public static void Until(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached: " + because);
            Thread.Sleep(10);
        }
    }

    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached: " + because);
            await Task.Delay(10);
        }
    }
}
