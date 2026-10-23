using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weighbridge.Core.Automation;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Weighing;
using Weighbridge.Devices.Simulation;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Identification;

public enum IdentificationKind
{
    /// <summary>RFID reader on serial/TCP that outputs the tag id as text (EPC/UID).</summary>
    Rfid = 0,

    /// <summary>Barcode / QR scanner in serial (virtual COM) mode.</summary>
    Scanner = 1,

    /// <summary>ANPR camera pushing plate results over HTTP (JSON, XML or multipart).</summary>
    AnprHttp = 2,

    Simulated = 3,
}

public sealed record IdentificationSourceConfig
{
    public string Id { get; init; } = "id1";
    public string Name { get; init; } = "Reader";
    public IdentificationKind Kind { get; init; } = IdentificationKind.Rfid;
    public bool Enabled { get; init; } = true;
    public ConnectionSettings Connection { get; init; } = new();
    public FramingOptions Framing { get; init; } = new();

    /// <summary>Extracts the identifier from a frame; group <c>id</c>.</summary>
    public string ExtractPattern { get; init; } = @"(?<id>[A-Za-z0-9\- ]{3,64})";

    /// <summary>For scanners: the code itself is the registration (e.g. QR printed for a vehicle). Otherwise it is looked up as a tag.</summary>
    public bool CodeIsRegistration { get; init; }

    // ANPR HTTP receiver
    public string ListenAddress { get; init; } = "0.0.0.0";
    public int ListenPort { get; init; } = 8085;

    /// <summary>Optional shared secret; when set, requests must carry <c>?token=…</c> or header <c>X-AWMS-Token</c>.</summary>
    public string? SharedToken { get; init; }

    public string PlatePattern { get; init; } =
        @"""(?:plate|plateNumber|licensePlate|license_plate|plate_number)""\s*:\s*""(?<plate>[^""]{2,20})""|<(?:licensePlate|plateNumber|PlateNumber)>(?<plate>[^<]{2,20})</";

    public string ConfidencePattern { get; init; } = @"""(?:confidence|confidenceLevel)""\s*:\s*(?<c>[\d.]+)|<confidenceLevel>(?<c>[\d.]+)</";

    /// <summary>Plates with a lower confidence (0–1, percentages are normalised) are ignored.</summary>
    public double MinConfidence { get; init; } = 0.7;

    /// <summary>The same identifier within this period is reported once.</summary>
    public int DebounceMs { get; init; } = 5000;
}

public interface IIdentificationSource : IDisposable
{
    string Name { get; }
    event EventHandler<VehicleIdentification>? Identified;
    void Start();
}

/// <summary>Suppresses repeated reads of the same tag/plate (RFID readers report many times per second).</summary>
internal sealed class Debouncer(int ms, TimeProvider time)
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public bool Accept(string key)
    {
        var now = time.GetUtcNow();
        lock (_seen)
        {
            if (_seen.TryGetValue(key, out var at) && (now - at).TotalMilliseconds < ms) return false;
            _seen[key] = now;
            if (_seen.Count > 500) _seen.Clear();
            return true;
        }
    }
}

/// <summary>RFID readers and barcode/QR scanners that send text over a serial port or TCP.</summary>
public sealed class SerialIdentificationSource : IIdentificationSource
{
    private readonly IdentificationSourceConfig _config;
    private readonly ITransportFactory _factory;
    private readonly Func<string, string?> _resolveTag;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Debouncer _debounce;
    private readonly Regex _extract;
    private Thread? _thread;
    private volatile bool _stop;

    public SerialIdentificationSource(IdentificationSourceConfig config, ITransportFactory factory, Func<string, string?> resolveTag, ILogger? log = null, TimeProvider? time = null)
    {
        _config = config;
        _factory = factory;
        _resolveTag = resolveTag;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _debounce = new Debouncer(config.DebounceMs, _time);
        _extract = new Regex(config.ExtractPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public string Name => _config.Name;
    public event EventHandler<VehicleIdentification>? Identified;

    /// <summary>Raised for tags/codes that do not map to a vehicle (shown to the operator).</summary>
    public event EventHandler<string>? UnknownCode;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ID " + _config.Name };
        _thread.Start();
    }

    /// <summary>Processes one frame of text (public for tests and keyboard-wedge scanners).</summary>
    public void Process(string text)
    {
        var m = _extract.Match(text.Trim());
        if (!m.Success) return;
        var code = m.Groups["id"].Success ? m.Groups["id"].Value.Trim() : m.Value.Trim();
        if (code.Length == 0 || !_debounce.Accept(code)) return;

        var registration = _config.CodeIsRegistration ? code : _resolveTag(code);
        if (registration is null)
        {
            UnknownCode?.Invoke(this, code);
            return;
        }
        var source = _config.Kind == IdentificationKind.Rfid ? "RFID" : "QR";
        Identified?.Invoke(this, new VehicleIdentification(VehicleRegistration.Display(registration), source, _time.GetUtcNow(), 1.0, code));
    }

    private void Run()
    {
        var buffer = new byte[1024];
        while (!_stop)
        {
            try
            {
                using var transport = _factory.Create(_config.Connection);
                transport.Open();
                var assembler = new FrameAssembler(_config.Framing);
                while (!_stop)
                {
                    var n = transport.Read(buffer, 100);
                    if (n == 0) continue;
                    foreach (var f in assembler.Feed(buffer.AsSpan(0, n), _time.GetUtcNow())) Process(Encoding.Latin1.GetString(f.Payload));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _log.LogWarning("{Name}: {Message}", _config.Name, e.Message);
                for (var i = 0; i < 60 && !_stop; i++) Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>
/// Minimal HTTP receiver for ANPR cameras that push plate events (Hikvision/Dahua "HTTP listening/alarm server",
/// generic JSON webhooks). Plates are extracted with configurable patterns; low-confidence reads are ignored.
/// </summary>
public sealed class AnprHttpReceiver : IIdentificationSource
{
    private const int MaxBody = 4 * 1024 * 1024;
    private readonly IdentificationSourceConfig _config;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Debouncer _debounce;
    private readonly Regex _plate;
    private readonly Regex _confidence;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public AnprHttpReceiver(IdentificationSourceConfig config, ILogger? log = null, TimeProvider? time = null)
    {
        _config = config;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _debounce = new Debouncer(config.DebounceMs, _time);
        _plate = new Regex(config.PlatePattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        _confidence = new Regex(config.ConfidencePattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    }

    public string Name => _config.Name;
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public event EventHandler<VehicleIdentification>? Identified;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Parse(_config.ListenAddress), _config.ListenPort);
        _listener.Start();
        _ = AcceptLoop(_cts.Token);
    }

    /// <summary>Extracts a plate from an event body (public for tests).</summary>
    public VehicleIdentification? Extract(string body)
    {
        var m = _plate.Match(body);
        if (!m.Success) return null;
        var plate = VehicleRegistration.Display(m.Groups["plate"].Value);
        if (plate.Length < 2 || plate.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) || plate.Trim('-', ' ').Length == 0) return null;
        double? confidence = null;
        var c = _confidence.Match(body);
        if (c.Success && double.TryParse(c.Groups["c"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            confidence = v > 1 ? v / 100.0 : v;
            if (confidence < _config.MinConfidence) return null;
        }
        return new VehicleIdentification(plate, "ANPR", _time.GetUtcNow(), confidence, m.Groups["plate"].Value);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Handle(client, ct);
        }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(10_000);
                var (requestLine, headers, body) = await ReadRequest(stream, timeout.Token).ConfigureAwait(false);
                var authorised = string.IsNullOrEmpty(_config.SharedToken)
                                 || requestLine.Contains("token=" + _config.SharedToken, StringComparison.Ordinal)
                                 || (headers.TryGetValue("x-awms-token", out var t) && t == _config.SharedToken);
                var status = authorised ? "200 OK" : "403 Forbidden";
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await stream.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
                if (!authorised) return;

                var id = Extract(body);
                if (id is not null && _debounce.Accept(VehicleRegistration.Normalize(id.Registration))) Identified?.Invoke(this, id);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or FormatException or RegexMatchTimeoutException)
            {
                _log.LogDebug("ANPR request failed: {Message}", e.Message);
            }
        }
    }

    private static async Task<(string RequestLine, Dictionary<string, string> Headers, string Body)> ReadRequest(NetworkStream stream, CancellationToken ct)
    {
        var data = new List<byte>(8192);
        var buffer = new byte[8192];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Connection closed.");
            data.AddRange(buffer.AsSpan(0, n).ToArray());
            headerEnd = IndexOf(data, "\r\n\r\n"u8);
            if (data.Count > 64 * 1024 && headerEnd < 0) throw new FormatException("Headers too large.");
        }
        var headerText = Encoding.ASCII.GetString(data.GetRange(0, headerEnd).ToArray());
        var lines = headerText.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
        if (length > MaxBody) throw new FormatException("Body too large.");
        var bodyStart = headerEnd + 4;
        while (data.Count - bodyStart < length)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) break;
            data.AddRange(buffer.AsSpan(0, n).ToArray());
        }
        var bodyBytes = data.GetRange(bodyStart, Math.Min(length, data.Count - bodyStart)).ToArray();
        // Multipart bodies contain binary images; Latin-1 keeps byte positions and the text parts readable.
        return (lines[0], headers, Encoding.Latin1.GetString(bodyBytes));
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
    }
}

/// <summary>Raises a plate read whenever the simulator drives a vehicle onto the deck.</summary>
public sealed class SimulatedIdentificationSource(SimulatorWorld world, TimeProvider? time = null) : IIdentificationSource
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => "Simulated ANPR";
    public event EventHandler<VehicleIdentification>? Identified;

    public void Start() => world.VehicleArriving += OnArriving;

    private void OnArriving(SimVehicle vehicle, bool loaded) =>
        Identified?.Invoke(this, new VehicleIdentification(vehicle.Registration, "ANPR", _time.GetUtcNow(), 0.97, vehicle.Registration));

    public void Dispose() => world.VehicleArriving -= OnArriving;
}
