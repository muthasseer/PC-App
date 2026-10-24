using Microsoft.Extensions.Logging;
using Weighbridge.Application.Security;
using Weighbridge.Core.Automation;
using Weighbridge.Devices.Cameras;
using Weighbridge.Devices.Identification;
using Weighbridge.Devices.Simulation;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Application.Hardware;

/// <summary>Captures evidence images from all configured cameras in parallel, within a time limit.</summary>
public sealed class CameraService(ISecretProtector protector, ILogger<CameraService> log, Func<CameraConfig, ICamera>? factory = null) : IDisposable
{
    private readonly Func<CameraConfig, ICamera> _factory = factory ?? (c => new HttpSnapshotCamera(c));
    private List<ICamera> _cameras = [];

    public IReadOnlyList<CameraConfig> Cameras => _cameras.Select(c => c.Config).ToList();

    public void Configure(IEnumerable<CameraConfig> configs)
    {
        foreach (var c in _cameras) c.Dispose();
        _cameras = configs.Select(c => _factory(c with { Password = Decrypt(c.Password) })).ToList();
    }

    public async Task<(IReadOnlyList<CameraSnapshot> Images, IReadOnlyList<string> Errors)> CaptureAllAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var targets = _cameras.Where(c => c.Config.CaptureOnWeighing).ToList();
        if (targets.Count == 0) return ([], []);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tasks = targets.Select(async c =>
        {
            try { return (Snap: await c.CaptureAsync(cts.Token).ConfigureAwait(false), Error: (string?)null); }
            catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or TaskCanceledException)
            {
                log.LogWarning("Camera {Camera}: {Message}", c.Config.Name, e.Message);
                return (Snap: (CameraSnapshot?)null, Error: $"{c.Config.Name}: {(e is OperationCanceledException ? "timed out" : e.Message)}");
            }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return (results.Where(r => r.Snap is not null).Select(r => r.Snap!).ToList(), results.Where(r => r.Error is not null).Select(r => r.Error!).ToList());
    }

    public async Task<CameraSnapshot?> PreviewAsync(string cameraId, CancellationToken ct = default)
    {
        var cam = _cameras.FirstOrDefault(c => c.Config.Id == cameraId);
        if (cam is null) return null;
        try { return await cam.CaptureAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException) { return null; }
    }

    private string? Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        try { return stored.StartsWith("dpapi:", StringComparison.Ordinal) || stored.StartsWith("aesgcm:", StringComparison.Ordinal) ? protector.Unprotect(stored) : stored; }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public void Dispose()
    {
        foreach (var c in _cameras) c.Dispose();
    }
}

/// <summary>Runs RFID/QR/ANPR sources and forwards identifications (operator entries too) to subscribers.</summary>
public sealed class IdentificationService(MasterDataService masterData, ITransportFactory transports, SimulatorWorld? world, TimeProvider time, ILoggerFactory loggers) : IDisposable
{
    private readonly List<IIdentificationSource> _sources = [];
    private readonly ILogger _log = loggers.CreateLogger<IdentificationService>();

    public event EventHandler<VehicleIdentification>? Identified;
    public event EventHandler<string>? UnknownCode;

    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToList();

    public void Configure(IEnumerable<IdentificationSourceConfig> configs)
    {
        Stop();
        foreach (var c in configs.Where(c => c.Enabled))
        {
            try
            {
                IIdentificationSource source = c.Kind switch
                {
                    IdentificationKind.AnprHttp => new AnprHttpReceiver(c, loggers.CreateLogger<AnprHttpReceiver>(), time),
                    IdentificationKind.Simulated when world is not null => new SimulatedIdentificationSource(world, time),
                    IdentificationKind.Simulated => throw new InvalidOperationException("The simulator is not available."),
                    _ => CreateSerial(c),
                };
                source.Identified += (_, id) => Identified?.Invoke(this, id);
                source.Start();
                _sources.Add(source);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
            {
                _log.LogWarning("Identification source {Name} could not start: {Message}", c.Name, e.Message);
            }
        }
    }

    private SerialIdentificationSource CreateSerial(IdentificationSourceConfig c)
    {
        var s = new SerialIdentificationSource(c, transports, masterData.ResolveTag, loggers.CreateLogger<SerialIdentificationSource>(), time);
        s.UnknownCode += (_, code) => UnknownCode?.Invoke(this, code);
        return s;
    }

    /// <summary>Vehicle number typed or scanned by the operator (keyboard-wedge scanners type into the vehicle field).</summary>
    public void Submit(string registration, string source = "Operator") =>
        Identified?.Invoke(this, new VehicleIdentification(Core.Weighing.VehicleRegistration.Display(registration), source, time.GetUtcNow(), 1.0, registration));

    public void Stop()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
    }

    public void Dispose() => Stop();
}
