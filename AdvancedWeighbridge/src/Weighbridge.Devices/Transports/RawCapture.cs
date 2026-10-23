using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weighbridge.Core.Common;

namespace Weighbridge.Devices.Transports;

/// <summary>
/// Raw serial capture file (<c>*.wbcap</c>, JSON lines): a header line followed by <c>{"t":ms,"hex":"..."}</c>
/// records. Used for sample-data replay, support cases and regression tests of new indicator profiles.
/// </summary>
public static class RawCaptureFormat
{
    public sealed record Header(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("started")] DateTimeOffset Started,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("note")] string? Note);

    public sealed record Record(
        [property: JsonPropertyName("t")] long OffsetMs,
        [property: JsonPropertyName("hex")] string Hex);

    public static (Header Header, List<(long OffsetMs, byte[] Data)> Records) Load(string path)
    {
        using var reader = new StreamReader(path);
        var first = reader.ReadLine() ?? throw new FormatException("The capture file is empty.");
        var header = JsonSerializer.Deserialize<Header>(first) ?? throw new FormatException("Invalid capture header.");
        if (header.Format != "wbcap") throw new FormatException("Not a weighbridge capture file.");
        var records = new List<(long, byte[])>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var r = JsonSerializer.Deserialize<Record>(line) ?? throw new FormatException("Invalid capture record.");
            records.Add((r.OffsetMs, ByteText.FromHex(r.Hex)));
        }
        return (header, records);
    }
}

public sealed class RawCaptureRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    public RawCaptureRecorder(string path, string? source, string? note = null)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        _writer.WriteLine(JsonSerializer.Serialize(new RawCaptureFormat.Header("wbcap", 1, DateTimeOffset.UtcNow, source, note)));
    }

    public string Path { get; }
    public long BytesRecorded { get; private set; }

    public void Record(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new RawCaptureFormat.Record(_clock.ElapsedMilliseconds, ByteText.ToHex(data, string.Empty))));
            BytesRecorded += data.Length;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}

/// <summary>Plays a capture file back in real time (optionally looping) as if the indicator were connected.</summary>
public sealed class ReplayTransport(string path, bool loop, double speed = 1.0) : IByteTransport
{
    private List<(long OffsetMs, byte[] Data)> _records = [];
    private int _index;
    private Stopwatch _clock = new();
    private long _loopOffset;

    public string Description => "Replay " + System.IO.Path.GetFileName(path);
    public bool IsOpen { get; private set; }

    public void Open()
    {
        _records = RawCaptureFormat.Load(path).Records;
        _index = 0;
        _loopOffset = 0;
        _clock = Stopwatch.StartNew();
        IsOpen = true;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_records.Count == 0)
        {
            Thread.Sleep(timeoutMs);
            return 0;
        }
        if (_index >= _records.Count)
        {
            if (!loop)
            {
                Thread.Sleep(timeoutMs);
                return 0;
            }
            _loopOffset += _records[^1].OffsetMs + 100;
            _index = 0;
        }

        var (offset, data) = _records[_index];
        var due = (long)((offset + _loopOffset) / Math.Max(0.01, speed));
        var wait = due - _clock.ElapsedMilliseconds;
        if (wait > 0)
        {
            Thread.Sleep((int)Math.Min(wait, timeoutMs));
            if (_clock.ElapsedMilliseconds < due) return 0;
        }
        var n = Math.Min(buffer.Length, data.Length);
        data.AsSpan(0, n).CopyTo(buffer);
        if (n < data.Length) _records[_index] = (offset, data[n..]);
        else _index++;
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
    }

    public void Dispose() => IsOpen = false;
}
