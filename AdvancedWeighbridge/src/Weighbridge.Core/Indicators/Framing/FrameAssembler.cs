using Weighbridge.Core.Common;

namespace Weighbridge.Core.Indicators.Framing;

public enum FramingMode
{
    /// <summary>A frame ends with a terminator such as CR, LF, CRLF or ETX. Most ASCII indicators.</summary>
    Terminator = 0,

    /// <summary>A frame starts with a start marker (e.g. STX) and ends with an end marker (e.g. CR or ETX),
    /// optionally followed by a fixed number of trailer bytes (checksum).</summary>
    StartEnd = 1,

    /// <summary>Every frame has the same length, optionally synchronised on a start marker.</summary>
    FixedLength = 2,

    /// <summary>A frame ends when the line has been silent for a configured gap (indicators without delimiters).</summary>
    IdleGap = 3,
}

public sealed record FramingOptions
{
    public FramingMode Mode { get; init; } = FramingMode.Terminator;

    /// <summary>Terminator in profile notation, e.g. <c>"\r\n"</c>, <c>"&lt;CR&gt;"</c> or <c>"&lt;ETX&gt;"</c>.</summary>
    public string Terminator { get; init; } = "\\r\\n";

    /// <summary>In <see cref="FramingMode.Terminator"/> mode, accept CR, LF or CRLF interchangeably.</summary>
    public bool AnyLineEnding { get; init; } = true;

    public string? StartMarker { get; init; }
    public string? EndMarker { get; init; }

    /// <summary>Bytes that follow the end marker and belong to the frame (e.g. 1 checksum byte).</summary>
    public int TrailerLength { get; init; }

    public int FixedLength { get; init; }

    public int IdleGapMs { get; init; } = 60;

    /// <summary>Protects against unbounded buffering when the delimiter never arrives (wrong settings, noise).</summary>
    public int MaxFrameLength { get; init; } = 512;
}

/// <summary>A complete frame cut from the byte stream.</summary>
public sealed record RawFrame(
    byte[] Payload,
    byte[] Full,
    byte[] Trailer,
    DateTimeOffset ReceivedAt,
    long Sequence)
{
    public string Display => ByteText.ToDisplay(Full);
    public string Hex => ByteText.ToHex(Full);

    public static RawFrame FromText(string text, DateTimeOffset at, long sequence = 0)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(text);
        return new RawFrame(bytes, bytes, [], at, sequence);
    }
}

public enum FramingIssueKind
{
    /// <summary>The buffer grew beyond <see cref="FramingOptions.MaxFrameLength"/> without a delimiter.</summary>
    Overflow,

    /// <summary>A new start marker arrived before the previous frame ended; the partial frame was dropped.</summary>
    IncompleteFrameDiscarded,

    /// <summary>Bytes outside any frame (before a start marker) were skipped.</summary>
    BytesSkipped,
}

public sealed record FramingIssue(FramingIssueKind Kind, int ByteCount, DateTimeOffset At);

/// <summary>
/// Incremental, allocation-light frame assembler. Feed it whatever chunks the transport delivers (fragments,
/// several frames at once, frames split across reads) and it yields complete frames. Not thread-safe: one
/// instance per connection, used from the receive loop only.
/// </summary>
public sealed class FrameAssembler
{
    private readonly FramingOptions _options;
    private readonly byte[] _terminator;
    private readonly byte[] _start;
    private readonly byte[] _end;
    private readonly List<byte> _buffer = new(256);
    private readonly List<FramingIssue> _issues = [];
    private bool _inFrame;
    private int _trailerRemaining;
    private int _endIndex = -1;
    private DateTimeOffset _lastByteAt = DateTimeOffset.MinValue;
    private long _sequence;

    public FrameAssembler(FramingOptions options)
    {
        _options = options;
        _terminator = ByteText.Unescape(options.Terminator);
        _start = ByteText.Unescape(options.StartMarker);
        _end = ByteText.Unescape(options.EndMarker);

        switch (options.Mode)
        {
            case FramingMode.Terminator when _terminator.Length == 0:
                throw new ArgumentException("Terminator framing needs a terminator.", nameof(options));
            case FramingMode.StartEnd when _start.Length == 0 || _end.Length == 0:
                throw new ArgumentException("Start/end framing needs both a start marker and an end marker.", nameof(options));
            case FramingMode.FixedLength when options.FixedLength <= 0:
                throw new ArgumentException("Fixed-length framing needs a positive frame length.", nameof(options));
            case FramingMode.IdleGap when options.IdleGapMs <= 0:
                throw new ArgumentException("Idle-gap framing needs a positive gap.", nameof(options));
        }
        if (options.MaxFrameLength < 4) throw new ArgumentException("MaxFrameLength is too small.", nameof(options));
    }

    public FramingOptions Options => _options;

    /// <summary>Issues found since the last call; the list is cleared by the call.</summary>
    public IReadOnlyList<FramingIssue> DrainIssues()
    {
        if (_issues.Count == 0) return [];
        var copy = _issues.ToArray();
        _issues.Clear();
        return copy;
    }

    public int BufferedByteCount => _buffer.Count;

    public void Reset()
    {
        _buffer.Clear();
        _inFrame = false;
        _trailerRemaining = 0;
        _endIndex = -1;
    }

    public IReadOnlyList<RawFrame> Feed(ReadOnlySpan<byte> data, DateTimeOffset receivedAt)
    {
        var frames = new List<RawFrame>();
        if (_options.Mode == FramingMode.IdleGap)
        {
            if (_buffer.Count > 0 && _lastByteAt != DateTimeOffset.MinValue
                && (receivedAt - _lastByteAt).TotalMilliseconds >= _options.IdleGapMs)
            {
                Emit(frames, _buffer.ToArray(), _buffer.ToArray(), [], _lastByteAt);
                _buffer.Clear();
            }
            foreach (var b in data)
            {
                _buffer.Add(b);
                if (_buffer.Count > _options.MaxFrameLength) Overflow(receivedAt);
            }
            if (!data.IsEmpty) _lastByteAt = receivedAt;
            return frames;
        }

        foreach (var b in data)
        {
            switch (_options.Mode)
            {
                case FramingMode.Terminator:
                    FeedTerminator(b, receivedAt, frames);
                    break;
                case FramingMode.StartEnd:
                    FeedStartEnd(b, receivedAt, frames);
                    break;
                case FramingMode.FixedLength:
                    FeedFixed(b, receivedAt, frames);
                    break;
            }
        }
        if (!data.IsEmpty) _lastByteAt = receivedAt;
        return frames;
    }

    /// <summary>Call periodically (e.g. every 20 ms) so idle-gap frames are released without waiting for more data.</summary>
    public IReadOnlyList<RawFrame> Flush(DateTimeOffset now)
    {
        if (_options.Mode != FramingMode.IdleGap || _buffer.Count == 0) return [];
        if ((now - _lastByteAt).TotalMilliseconds < _options.IdleGapMs) return [];
        var frames = new List<RawFrame>(1);
        var bytes = _buffer.ToArray();
        Emit(frames, bytes, bytes, [], _lastByteAt);
        _buffer.Clear();
        return frames;
    }

    private void FeedTerminator(byte b, DateTimeOffset at, List<RawFrame> frames)
    {
        var isLineTerminator = _options.AnyLineEnding && IsLineEndingTerminator() && (b == 0x0D || b == 0x0A);
        if (isLineTerminator)
        {
            // CR, LF or CRLF all end a frame. Empty frames (the LF of a CRLF) are ignored.
            if (_buffer.Count > 0)
            {
                var payload = _buffer.ToArray();
                var full = new byte[payload.Length + 1];
                payload.CopyTo(full, 0);
                full[^1] = b;
                Emit(frames, payload, full, [], at);
                _buffer.Clear();
            }
            return;
        }

        _buffer.Add(b);
        if (EndsWith(_buffer, _terminator))
        {
            var full = _buffer.ToArray();
            var payload = full[..^_terminator.Length];
            if (payload.Length > 0) Emit(frames, payload, full, [], at);
            _buffer.Clear();
            return;
        }
        if (_buffer.Count > _options.MaxFrameLength) Overflow(at);
    }

    private bool IsLineEndingTerminator() =>
        _terminator.All(t => t is 0x0D or 0x0A);

    private void FeedStartEnd(byte b, DateTimeOffset at, List<RawFrame> frames)
    {
        if (_trailerRemaining > 0)
        {
            _buffer.Add(b);
            _trailerRemaining--;
            if (_trailerRemaining == 0) EmitStartEnd(frames, at);
            return;
        }

        _buffer.Add(b);
        if (!_inFrame)
        {
            if (EndsWith(_buffer, _start))
            {
                var skipped = _buffer.Count - _start.Length;
                if (skipped > 0) _issues.Add(new FramingIssue(FramingIssueKind.BytesSkipped, skipped, at));
                _buffer.Clear();
                _buffer.AddRange(_start);
                _inFrame = true;
            }
            else if (_buffer.Count > _options.MaxFrameLength)
            {
                _issues.Add(new FramingIssue(FramingIssueKind.BytesSkipped, _buffer.Count - _start.Length, at));
                // Keep only what could still be the beginning of a start marker.
                var keep = _buffer.Skip(_buffer.Count - (_start.Length - 1)).ToList();
                _buffer.Clear();
                _buffer.AddRange(keep);
            }
            return;
        }

        // Inside a frame: a second start marker means the previous frame was cut short (resynchronise).
        // Only check once enough bytes beyond the first marker exist, and never when start == end.
        if (_buffer.Count >= _start.Length * 2 && !_start.AsSpan().SequenceEqual(_end) && EndsWith(_buffer, _start))
        {
            _issues.Add(new FramingIssue(FramingIssueKind.IncompleteFrameDiscarded, _buffer.Count - _start.Length, at));
            _buffer.Clear();
            _buffer.AddRange(_start);
            return;
        }

        if (_buffer.Count >= _start.Length + _end.Length && EndsWith(_buffer, _end))
        {
            _endIndex = _buffer.Count;
            if (_options.TrailerLength > 0)
            {
                _trailerRemaining = _options.TrailerLength;
                return;
            }
            EmitStartEnd(frames, at);
            return;
        }

        if (_buffer.Count > _options.MaxFrameLength)
        {
            Overflow(at);
            _inFrame = false;
        }
    }

    private void EmitStartEnd(List<RawFrame> frames, DateTimeOffset at)
    {
        var full = _buffer.ToArray();
        var payloadStart = _start.Length;
        var payloadEnd = _endIndex - _end.Length;
        var payload = payloadEnd > payloadStart ? full[payloadStart..payloadEnd] : [];
        var trailer = full[_endIndex..];
        Emit(frames, payload, full, trailer, at);
        _buffer.Clear();
        _inFrame = false;
        _endIndex = -1;
    }

    private void FeedFixed(byte b, DateTimeOffset at, List<RawFrame> frames)
    {
        _buffer.Add(b);
        if (_start.Length > 0 && !_inFrame)
        {
            if (EndsWith(_buffer, _start))
            {
                var skipped = _buffer.Count - _start.Length;
                if (skipped > 0) _issues.Add(new FramingIssue(FramingIssueKind.BytesSkipped, skipped, at));
                _buffer.Clear();
                _buffer.AddRange(_start);
                _inFrame = true;
            }
            else if (_buffer.Count > _options.MaxFrameLength)
            {
                _issues.Add(new FramingIssue(FramingIssueKind.BytesSkipped, _buffer.Count, at));
                _buffer.Clear();
            }
            return;
        }

        if (_buffer.Count == _options.FixedLength)
        {
            var full = _buffer.ToArray();
            var payload = _start.Length > 0 ? full[_start.Length..] : full;
            Emit(frames, payload, full, [], at);
            _buffer.Clear();
            _inFrame = false;
        }
    }

    private void Emit(List<RawFrame> frames, byte[] payload, byte[] full, byte[] trailer, DateTimeOffset at)
    {
        _sequence++;
        frames.Add(new RawFrame(payload, full, trailer, at, _sequence));
    }

    private void Overflow(DateTimeOffset at)
    {
        _issues.Add(new FramingIssue(FramingIssueKind.Overflow, _buffer.Count, at));
        _buffer.Clear();
    }

    private static bool EndsWith(List<byte> buffer, byte[] suffix)
    {
        if (suffix.Length == 0 || buffer.Count < suffix.Length) return false;
        var offset = buffer.Count - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (buffer[offset + i] != suffix[i]) return false;
        }
        return true;
    }
}
