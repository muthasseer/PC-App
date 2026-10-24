using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Indicator;

public sealed record RawChunk(DateTimeOffset At, byte[] Data);

public sealed record IndicatorConnectionEvent(DateTimeOffset At, ConnectionState State, string Message);

/// <summary>
/// Owns one indicator connection. A dedicated background thread reads bytes, assembles frames, validates
/// checksums, parses, tracks stability and publishes an immutable <see cref="IndicatorSnapshot"/>. Readers
/// (UI, GET WEIGHT, automation) read <see cref="Current"/> without locks; nothing here touches the UI thread.
/// </summary>
public sealed class IndicatorService : IDisposable
{
    private const int ReadSliceMs = 40;
    private const int RingSize = 512;

    private readonly IndicatorProfile _profile;
    private readonly ITransportFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ProtocolRegistry? _protocols;
    private readonly ConcurrentQueue<byte[]> _outgoing = new();
    private readonly DecodedFrame?[] _ring = new DecodedFrame?[RingSize];
    private readonly object _ringGate = new();
    private long _ringCount;
    private Thread? _thread;
    private volatile bool _stop;
    private IndicatorSnapshot _snapshot;
    private long _reconnects;

    public IndicatorService(IndicatorProfile profile, ITransportFactory factory, TimeProvider? time = null, ILogger? log = null, ProtocolRegistry? protocols = null)
    {
        var problems = profile.Validate(protocols);
        if (problems.Count > 0) throw new ArgumentException("Invalid indicator profile: " + string.Join(" ", problems), nameof(profile));
        _profile = profile;
        _factory = factory;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _protocols = protocols;
        _snapshot = BaseSnapshot(ConnectionState.Disconnected, _time.GetUtcNow());
    }

    public IndicatorProfile Profile => _profile;
    public IndicatorSnapshot Current => Volatile.Read(ref _snapshot);
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Every decoded frame (high rate; for the raw-data monitor). Raised on the receive thread.</summary>
    public event EventHandler<DecodedFrame>? FrameDecoded;

    /// <summary>Raw bytes as received (for hex view and capture recording). Raised on the receive thread.</summary>
    public event EventHandler<RawChunk>? BytesReceived;

    public event EventHandler<IndicatorConnectionEvent>? ConnectionChanged;

    public event EventHandler<IReadOnlyList<FramingIssue>>? FramingIssuesDetected;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Already started.");
        _stop = false;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Indicator " + _profile.Name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop(TimeSpan? wait = null)
    {
        _stop = true;
        var t = _thread;
        if (t is not null && !t.Join(wait ?? TimeSpan.FromSeconds(5))) _log.LogWarning("Indicator thread did not stop in time.");
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Queues bytes for the indicator (setup wizard tests, zero/tare commands of supporting indicators).</summary>
    public void Send(ReadOnlySpan<byte> data) => _outgoing.Enqueue(data.ToArray());

    public IReadOnlyList<DecodedFrame> RecentFrames(int max = 100)
    {
        lock (_ringGate)
        {
            var count = (int)Math.Min(Math.Min(_ringCount, RingSize), max);
            var list = new List<DecodedFrame>(count);
            for (var i = count; i >= 1; i--)
            {
                var f = _ring[(int)((_ringCount - i) % RingSize)];
                if (f is not null) list.Add(f);
            }
            return list;
        }
    }

    /// <summary>The frames up to and including <paramref name="sequence"/> (the evidence stored with a capture).</summary>
    public IReadOnlyList<RawFrame> EvidenceFor(long sequence, int count) =>
        RecentFrames(RingSize).Where(f => f.Frame.Sequence <= sequence && f.Frame.Sequence > sequence - count).Select(f => f.Frame).ToList();

    // ------------------------------------------------------------------ receive loop

    private void Run()
    {
        var backoff = Math.Max(250, _profile.Connection.ReconnectIntervalMs);
        var pollBytes = _profile.Poll.Enabled ? ByteText.Unescape(_profile.Poll.Command) : [];
        var buffer = new byte[4096];
        var firstAttempt = true;

        while (!_stop)
        {
            if (!firstAttempt) Interlocked.Increment(ref _reconnects);
            firstAttempt = false;
            Publish(BaseSnapshot(ConnectionState.Connecting, _time.GetUtcNow()) with { LastError = Current.LastError });
            IByteTransport? transport = null;
            try
            {
                transport = _factory.Create(_profile.Connection);
                transport.Open();
                var openedAt = _time.GetUtcNow();
                RaiseConnection(ConnectionState.Connecting, $"{transport.Description} opened.");
                _log.LogInformation("Indicator {Name}: {Port} opened", _profile.Name, transport.Description);
                backoff = Math.Max(250, _profile.Connection.ReconnectIntervalMs);

                var decoder = new IndicatorDecoder(_profile, _protocols);
                var stability = new StabilityDetector(_profile.Stability);
                var state = new LoopState { OpenedAt = openedAt, Port = transport.Description };
                var nextPoll = openedAt;

                while (!_stop)
                {
                    while (_outgoing.TryDequeue(out var outgoing)) transport.Write(outgoing);
                    var now = _time.GetUtcNow();
                    if (pollBytes.Length > 0 && now >= nextPoll)
                    {
                        transport.Write(pollBytes);
                        nextPoll = now.AddMilliseconds(Math.Max(20, _profile.Poll.IntervalMs));
                    }

                    var n = transport.Read(buffer, ReadSliceMs);
                    now = _time.GetUtcNow();
                    var changed = false;
                    if (n > 0)
                    {
                        state.LastByteAt = now;
                        var chunk = buffer.AsSpan(0, n).ToArray();
                        Raise(BytesReceived, new RawChunk(now, chunk));
                        foreach (var f in decoder.Feed(chunk, now)) changed |= Handle(f, stability, state);
                    }
                    foreach (var f in decoder.Flush(now)) changed |= Handle(f, stability, state);

                    var issues = decoder.Assembler.DrainIssues();
                    if (issues.Count > 0)
                    {
                        state.FramingIssues += issues.Count;
                        Raise(FramingIssuesDetected, issues);
                    }

                    var connection = Classify(state, now);
                    if (connection != state.LastPublishedState)
                    {
                        if (connection == ConnectionState.Silent) RaiseConnection(connection, $"No data from {transport.Description} for {_profile.Connection.ReadTimeoutMs} ms.");
                        else if (connection == ConnectionState.Connected) RaiseConnection(connection, $"Receiving data on {transport.Description}.");
                        changed = true;
                    }
                    if (!changed && (now - state.LastPublishAt).TotalMilliseconds < 250) continue;

                    PublishState(state, connection, stability.Evaluate(now), now);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var message = e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException
                    ? e.Message
                    : $"{e.GetType().Name}: {e.Message}";
                _log.LogWarning("Indicator {Name}: {Message}", _profile.Name, message);
                Publish(BaseSnapshot(ConnectionState.Error, _time.GetUtcNow()) with { LastError = message, ReconnectCount = Interlocked.Read(ref _reconnects) });
                RaiseConnection(ConnectionState.Error, message);
            }
            finally
            {
                try { transport?.Dispose(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }

            // Back off before reconnecting; checking the stop flag frequently.
            var until = Environment.TickCount64 + backoff;
            while (!_stop && Environment.TickCount64 < until) Thread.Sleep(50);
            backoff = Math.Min(backoff * 2, 30_000);
        }

        Publish(BaseSnapshot(ConnectionState.Disconnected, _time.GetUtcNow()));
        RaiseConnection(ConnectionState.Disconnected, "Stopped.");
    }

    private sealed class LoopState
    {
        public DateTimeOffset OpenedAt;
        public DateTimeOffset? LastByteAt;
        public DateTimeOffset? LastFrameAt;
        public DateTimeOffset LastPublishAt = DateTimeOffset.MinValue;
        public ConnectionState LastPublishedState = ConnectionState.Connecting;
        public WeightReading? LastReading;
        public string? LastRejectReason;
        public DateTimeOffset? LastRejectAt;
        public long? LastRejectSequence;
        public long Parsed;
        public long Rejected;
        public long FramingIssues;
        public string Port = string.Empty;
    }

    private bool Handle(DecodedFrame f, StabilityDetector stability, LoopState state)
    {
        lock (_ringGate)
        {
            _ring[(int)(_ringCount % RingSize)] = f;
            _ringCount++;
        }
        Raise(FrameDecoded, f);
        state.LastFrameAt = f.Frame.ReceivedAt;
        switch (f.Result.Outcome)
        {
            case ParseOutcome.Parsed:
                state.Parsed++;
                state.LastReading = f.Result.Reading;
                stability.Add(f.Result.Reading!);
                return true;
            case ParseOutcome.Rejected:
                state.Rejected++;
                state.LastRejectReason = f.Result.Reason;
                state.LastRejectAt = f.Frame.ReceivedAt;
                state.LastRejectSequence = f.Frame.Sequence;
                return true;
            default:
                return false;
        }
    }

    private ConnectionState Classify(LoopState s, DateTimeOffset now)
    {
        var timeout = Math.Max(200, _profile.Connection.ReadTimeoutMs);
        if (s.LastByteAt is { } last) return (now - last).TotalMilliseconds <= timeout ? ConnectionState.Connected : ConnectionState.Silent;
        return (now - s.OpenedAt).TotalMilliseconds <= timeout ? ConnectionState.Connecting : ConnectionState.Silent;
    }

    private void PublishState(LoopState s, ConnectionState connection, StabilityState stability, DateTimeOffset now)
    {
        s.LastPublishAt = now;
        s.LastPublishedState = connection;
        Publish(BaseSnapshot(connection, now) with
        {
            Latest = s.LastReading,
            Stability = stability,
            LastFrameAt = s.LastFrameAt,
            LastRejectReason = s.LastRejectReason,
            LastRejectAt = s.LastRejectAt,
            LastRejectSequence = s.LastRejectSequence,
            FramesParsed = s.Parsed,
            FramesRejected = s.Rejected,
            ReconnectCount = Interlocked.Read(ref _reconnects),
            PortDescription = s.Port,
            LastError = connection == ConnectionState.Silent ? "No data received (check cable, baud rate, or whether the indicator needs a request command)." : null,
        });
    }

    private IndicatorSnapshot BaseSnapshot(ConnectionState state, DateTimeOffset at) => new()
    {
        Connection = state,
        Timestamp = at,
        ProfileId = _profile.Id,
        ProfileName = _profile.Name,
        ProfileVersion = _profile.Version,
        PortDescription = _profile.Connection.Transport switch
        {
            TransportKind.Serial => _profile.Connection.Serial.Summary,
            TransportKind.TcpClient => $"tcp://{_profile.Connection.Tcp.Host}:{_profile.Connection.Tcp.Port}",
            _ => _profile.Connection.Transport.ToString(),
        },
    };

    private void Publish(IndicatorSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    private void RaiseConnection(ConnectionState state, string message) => Raise(ConnectionChanged, new IndicatorConnectionEvent(_time.GetUtcNow(), state, message));

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null) return;
        try
        {
            handler(this, args);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A faulty subscriber must never stop weight reception.
            _log.LogError(e, "Indicator event handler failed");
        }
    }
}
