using Microsoft.Extensions.Logging;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Devices.Indicator;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Application.Hardware;

/// <summary>
/// Runs the indicator of the active weighbridge. Connection changes are logged to device_events (one row per change,
/// rejected-frame bursts summarised) for the communication error report; long outages raise notifications.
/// </summary>
public sealed class IndicatorManager(
    Database db,
    SettingsService settings,
    NotificationService notifications,
    ITransportFactory transports,
    ProtocolRegistry protocols,
    TimeProvider time,
    ILoggerFactory loggers) : IDisposable
{
    private readonly ILogger _log = loggers.CreateLogger<IndicatorManager>();
    private readonly object _gate = new();
    private IndicatorService? _service;
    private long _weighbridgeId;
    private Timer? _monitor;
    private long _rejectedLogged;
    private DateTimeOffset? _problemSince;
    private bool _problemNotified;

    public IndicatorService? Current { get { lock (_gate) return _service; } }
    public IndicatorProfile? Profile => Current?.Profile;
    public IndicatorSnapshot Snapshot => Current?.Current ?? IndicatorSnapshot.Disconnected(time.GetUtcNow());

    public event EventHandler<IndicatorService?>? ServiceChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var (profile, port) = await db.ReadAsync(async s =>
        {
            var device = await SiteStore.DeviceForWeighbridgeAsync(s, weighing.ActiveWeighbridgeId, ct).ConfigureAwait(false);
            if (device is null) return ((IndicatorProfile?)null, (string?)null);
            var entity = await SiteStore.GetProfileAsync(s, device.ProfileId, ct).ConfigureAwait(false);
            return (entity?.ToProfile(), device.PortOverride);
        }, ct).ConfigureAwait(false);
        _weighbridgeId = weighing.ActiveWeighbridgeId;
        if (profile is null)
        {
            _log.LogWarning("No indicator profile is assigned to the active weighbridge.");
            Replace(null);
            return;
        }
        if (!string.IsNullOrWhiteSpace(port)) profile = profile with { Connection = profile.Connection with { Serial = profile.Connection.Serial with { PortName = port } } };
        Start(profile);
    }

    /// <summary>Starts (or restarts) with a specific profile.</summary>
    public void Start(IndicatorProfile profile)
    {
        var service = new IndicatorService(profile, transports, time, loggers.CreateLogger<IndicatorService>(), protocols);
        service.ConnectionChanged += OnConnectionChanged;
        Replace(service);
        service.Start();
        _monitor ??= new Timer(_ => Monitor(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Stop() => Replace(null);

    private void Replace(IndicatorService? next)
    {
        IndicatorService? old;
        lock (_gate)
        {
            old = _service;
            _service = next;
            _rejectedLogged = 0;
        }
        if (old is not null)
        {
            old.ConnectionChanged -= OnConnectionChanged;
            old.Stop();
        }
        ServiceChanged?.Invoke(this, next);
    }

    private void OnConnectionChanged(object? sender, IndicatorConnectionEvent e)
    {
        if (e.State is ConnectionState.Connecting) return;
        _ = Record(e.State.ToString(), e.Message);
    }

    private void Monitor()
    {
        var s = Current?.Current;
        if (s is null) return;
        var now = time.GetUtcNow();
        // Summarise rejected frames instead of logging each one.
        var newRejected = s.FramesRejected - _rejectedLogged;
        if (newRejected > 0)
        {
            _rejectedLogged = s.FramesRejected;
            _ = Record("RejectedFrames", $"{newRejected} invalid frame(s) in the last interval. Last: {s.LastRejectReason}");
        }
        var problem = s.Connection is ConnectionState.Error or ConnectionState.Silent or ConnectionState.Disconnected;
        if (!problem)
        {
            _problemSince = null;
            _problemNotified = false;
            return;
        }
        _problemSince ??= now;
        if (!_problemNotified && (now - _problemSince.Value).TotalSeconds >= 15)
        {
            _problemNotified = true;
            _ = notifications.NotifyAsync("Error", "Indicator", "Weight indicator not available", $"{s.PortDescription}: {s.LastError ?? s.Connection.ToString()}");
        }
    }

    private async Task Record(string type, string message)
    {
        try
        {
            await db.InTransactionAsync(s => DeviceEventStore.AddAsync(s, new DeviceEventEntity
            {
                AtUtc = time.GetUtcNow(), WeighbridgeId = _weighbridgeId == 0 ? null : _weighbridgeId, Device = Profile?.Name ?? "indicator", EventType = type,
                Message = message.Length > 500 ? message[..500] : message,
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Device event could not be stored");
        }
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        Replace(null);
    }
}
