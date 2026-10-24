using Weighbridge.Application.Settings;
using Weighbridge.Core.Indicators;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Maintenance;

public sealed record HealthReport(
    DateTimeOffset At,
    double DiskFreeGb,
    bool DatabaseOk,
    string DatabaseProvider,
    int SchemaVersion,
    DateTimeOffset? LastBackupUtc,
    SyncQueueStore.Counts Sync,
    ConnectionState Indicator,
    bool PreviousShutdownUnclean,
    IReadOnlyList<string> Warnings)
{
    public bool Healthy => Warnings.Count == 0;
}

/// <summary>System health shown in the status bar, the About page, diagnostics exports and the cloud heartbeat.</summary>
public sealed class HealthService(Database db, SettingsService settings, AppPaths paths, TimeProvider time)
{
    private bool? _unclean;

    /// <summary>Call at start-up: detects whether the previous run ended without a clean shutdown.</summary>
    public bool MarkRunning()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RunMarkerFile)!);
        _unclean = File.Exists(paths.RunMarkerFile);
        File.WriteAllText(paths.RunMarkerFile, time.GetUtcNow().ToString("O"));
        return _unclean.Value;
    }

    public void MarkStopped()
    {
        try { File.Delete(paths.RunMarkerFile); }
        catch (IOException) { }
    }

    public async Task<HealthReport> CheckAsync(ConnectionState indicator, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        double freeGb = -1;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(paths.Root));
            if (root is not null)
            {
                freeGb = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
                if (freeGb < 2) warnings.Add($"Low disk space: {freeGb:0.0} GB free on {root}.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add("Disk space could not be checked: " + e.Message);
        }

        var dbOk = true;
        var schema = 0;
        DateTimeOffset? lastBackup = null;
        var sync = new SyncQueueStore.Counts(0, 0, 0, 0, null);
        try
        {
            schema = await MigrationRunner.CurrentVersionAsync(db, ct).ConfigureAwait(false);
            lastBackup = (await db.ReadAsync(s => BackupHistoryStore.LastSuccessfulAsync(s, ct), ct).ConfigureAwait(false))?.CompletedUtc;
            sync = await db.ReadAsync(s => SyncQueueStore.CountsAsync(s, ct), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            dbOk = false;
            warnings.Add("Database problem: " + e.Message);
        }

        var now = time.GetUtcNow();
        var backup = await settings.GetAsync<BackupSettings>(SettingKeys.Backup, ct).ConfigureAwait(false);
        if (db.Options.Provider == DatabaseProvider.Sqlite && backup.ScheduleEnabled && (lastBackup is null || (now - lastBackup.Value).TotalHours > 48))
        {
            warnings.Add(lastBackup is null ? "No backup has been made yet." : $"The last backup is {(now - lastBackup.Value).TotalDays:0} days old.");
        }
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        if (cloud.Enabled && sync.OldestPending is { } oldest && (now - oldest).TotalHours > 6) warnings.Add($"Cloud synchronisation is {(now - oldest).TotalHours:0} hours behind ({sync.Pending + sync.Failed} items waiting).");
        if (sync.Conflicts > 0) warnings.Add($"{sync.Conflicts} cloud synchronisation conflict(s) need review.");
        if (indicator is ConnectionState.Error or ConnectionState.Disconnected or ConnectionState.Silent) warnings.Add($"Indicator: {indicator}.");
        if (_unclean == true) warnings.Add("The application was not shut down cleanly last time. Pending weighings were recovered from the database.");

        return new HealthReport(now, freeGb, dbOk, db.Options.Provider.ToString(), schema, lastBackup, sync, indicator, _unclean == true, warnings);
    }
}
