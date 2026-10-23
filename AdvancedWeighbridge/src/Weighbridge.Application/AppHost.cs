using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weighbridge.Application.Automation;
using Weighbridge.Application.Hardware;
using Weighbridge.Application.History;
using Weighbridge.Application.Maintenance;
using Weighbridge.Application.Reports;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Sync;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Devices.IO;
using Weighbridge.Devices.Plugins;
using Weighbridge.Devices.Simulation;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Application;

public sealed record AppHostOptions
{
    public required AppPaths Paths { get; init; }

    /// <summary>Defaults to the SQLite file under <see cref="AppPaths.DatabaseFile"/>.</summary>
    public DatabaseOptions? Database { get; init; }

    public ILoggerFactory? Logging { get; init; }
    public TimeProvider? Time { get; init; }
    public HttpMessageHandler? CloudHandler { get; init; }
    public ISecretProtector? Protector { get; init; }
}

/// <summary>
/// Composition root shared by the desktop application, the command-line self-test and integration tests.
/// Creating the host migrates the database (with a safety backup first when an existing database is upgraded).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private Timer? _scheduler;

    private AppHost(AppHostOptions options, Database db, ILoggerFactory loggers, TimeProvider time, ISecretProtector protector)
    {
        _loggers = loggers;
        Paths = options.Paths;
        Database = db;
        Time = time;
        Session = new SessionContext(time);
        Audit = new AuditService(db, time, loggers.CreateLogger<AuditService>());
        Settings = new SettingsService(db, protector, Audit, time);
        Notifications = new NotificationService(db, time);
        Auth = new AuthService(db, Settings, Audit, protector, time);
        Setup = new SetupService(db, Settings, Audit, time);
        MasterData = new MasterDataService(db, Audit, time);
        Weighing = new WeighingService(db, Settings, Audit, Notifications, options.Paths, time, loggers.CreateLogger<WeighingService>());
        Tickets = new TicketService(db, Settings, Audit, options.Paths, time);
        History = new HistoryService(db, options.Paths);
        Reports = new ReportService(db, Settings, Audit, time);
        Backups = new BackupService(db, Settings, Audit, Notifications, options.Paths, time, loggers.CreateLogger<BackupService>());
        Health = new HealthService(db, Settings, options.Paths, time);
        Simulator = new SimulatorWorld(new Core.Indicators.SimulatorSettings(), time);
        Transports = new TransportFactory(Simulator, time);
        Protocols = ProtocolRegistry.CreateDefault();
        Indicator = new IndicatorManager(db, Settings, Notifications, Transports, Protocols, time, loggers);
        Cameras = new CameraService(protector, loggers.CreateLogger<CameraService>());
        Identification = new IdentificationService(MasterData, Transports, Simulator, time, loggers);
        CloudHandler = options.CloudHandler;
    }

    public AppPaths Paths { get; }
    public Database Database { get; }
    public TimeProvider Time { get; }
    public SessionContext Session { get; }
    public AuditService Audit { get; }
    public SettingsService Settings { get; }
    public NotificationService Notifications { get; }
    public AuthService Auth { get; }
    public SetupService Setup { get; }
    public MasterDataService MasterData { get; }
    public WeighingService Weighing { get; }
    public TicketService Tickets { get; }
    public HistoryService History { get; }
    public ReportService Reports { get; }
    public BackupService Backups { get; }
    public HealthService Health { get; }
    public SimulatorWorld Simulator { get; }
    public TransportFactory Transports { get; }
    public ProtocolRegistry Protocols { get; }
    public IndicatorManager Indicator { get; }
    public CameraService Cameras { get; }
    public IdentificationService Identification { get; }
    public HardwareController? Hardware { get; private set; }
    public AutomationController? Automation { get; private set; }
    public CloudSyncService? Cloud { get; private set; }
    public IReadOnlyList<PluginLoader.LoadedPlugin> Plugins { get; private set; } = [];
    private HttpMessageHandler? CloudHandler { get; }

    public ILogger<T> Logger<T>() => _loggers.CreateLogger<T>();

    public static async Task<AppHost> CreateAsync(AppHostOptions options, CancellationToken ct = default)
    {
        options.Paths.EnsureCreated();
        var time = options.Time ?? TimeProvider.System;
        var loggers = options.Logging ?? NullLoggerFactory.Instance;
        var dbOptions = options.Database ?? DatabaseOptions.SqliteFile(options.Paths.DatabaseFile);
        var db = new Database(dbOptions);
        var log = loggers.CreateLogger<AppHost>();

        await MigrationRunner.MigrateAsync(db, loggers.CreateLogger("Migrations"), async fromVersion =>
        {
            if (dbOptions.Provider != DatabaseProvider.Sqlite) return;
            log.LogInformation("Creating a safety backup before upgrading the database from schema {Version}", fromVersion);
            await SqliteBackup.CreateAsync(db, Path.Combine(options.Paths.BackupDirectory, "pre-upgrade"), null, BackupService.AppVersion, time.GetUtcNow(), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var protector = options.Protector ?? SecretProtectors.Create(options.Paths.KeyDirectory);
        var host = new AppHost(options, db, loggers, time, protector);
        host.Plugins = PluginLoader.LoadProtocols(options.Paths.PluginDirectory, host.Protocols, log);
        return host;
    }

    /// <summary>Starts the indicator, I/O, cameras, identification, automation, cloud sync and the backup scheduler.</summary>
    public async Task StartDevicesAsync(UserContext actor, CancellationToken ct = default)
    {
        Health.MarkRunning();
        await Indicator.StartAsync(ct).ConfigureAwait(false);

        var hardware = await Settings.GetAsync<HardwareConfig>(SettingKeys.Hardware, ct).ConfigureAwait(false);
        if (hardware.Controllers.Count > 0)
        {
            Hardware = new HardwareController(hardware, HardwareController.DefaultFactory(Simulator), Logger<HardwareController>(), Time);
        }
        Cameras.Configure(hardware.Cameras);
        Identification.Configure(hardware.Identification);

        var weighing = await Settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        Automation = new AutomationController(Indicator, Hardware, Weighing, Cameras, Identification, Settings, Audit, Notifications, Time, Logger<AutomationController>());
        var mode = weighing.DefaultMode == OperatingMode.Automatic && !actor.Has(Permissions.WeighingAutomatic) ? OperatingMode.SemiAutomatic : weighing.DefaultMode;
        await Automation.StartAsync(mode, actor, ct).ConfigureAwait(false);

        Cloud = new CloudSyncService(Database, Settings, Weighing, Indicator, () => Automation?.Status, Health, Audit, Time, Logger<CloudSyncService>(), CloudHandler);
        Cloud.Start();

        _scheduler = new Timer(_ => _ = RunScheduledAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private async Task RunScheduledAsync()
    {
        try
        {
            await Backups.RunScheduledIfDueAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger<AppHost>().LogError(e, "Scheduled backup failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _scheduler?.Dispose();
        Cloud?.Dispose();
        if (Automation is not null) await Automation.DisposeAsync().ConfigureAwait(false);
        Identification.Dispose();
        Cameras.Dispose();
        Indicator.Dispose();
        Hardware?.Dispose();
        Health.MarkStopped();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
