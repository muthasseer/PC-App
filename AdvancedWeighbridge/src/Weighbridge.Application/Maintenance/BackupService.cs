using System.Globalization;
using Microsoft.Extensions.Logging;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Maintenance;

/// <summary>Manual and scheduled backups with history, a second copy location and pruning of old backup files only.</summary>
public sealed class BackupService(Database db, SettingsService settings, AuditService audit, NotificationService notifications, AppPaths paths, TimeProvider time, ILogger<BackupService> log)
{
    public static string AppVersion => typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<BackupInfo> BackupNowAsync(UserContext user, string kind = "Manual", CancellationToken ct = default)
    {
        if (kind == "Manual") user.Demand(Permissions.BackupManage);
        var cfg = await settings.GetAsync<BackupSettings>(SettingKeys.Backup, ct).ConfigureAwait(false);
        var dir = string.IsNullOrWhiteSpace(cfg.Directory) ? paths.BackupDirectory : cfg.Directory;
        var now = time.GetUtcNow();
        var historyId = await db.InTransactionAsync(s => BackupHistoryStore.StartAsync(s, new BackupHistoryEntity
        {
            StartedUtc = now, Kind = kind, FilePath = dir, Status = "Running", InitiatedBy = user.UserId == 0 ? null : user.UserId,
        }, ct), ct).ConfigureAwait(false);
        try
        {
            var info = await SqliteBackup.CreateAsync(db, dir, cfg.IncludeEvidence ? paths.EvidenceDirectory : null, AppVersion, now, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cfg.SecondaryDirectory))
            {
                try
                {
                    Directory.CreateDirectory(cfg.SecondaryDirectory);
                    File.Copy(info.Path, Path.Combine(cfg.SecondaryDirectory, Path.GetFileName(info.Path)), overwrite: false);
                    File.Copy(info.Path + ".sha256", Path.Combine(cfg.SecondaryDirectory, Path.GetFileName(info.Path) + ".sha256"), overwrite: false);
                    SqliteBackup.Prune(cfg.SecondaryDirectory, cfg.KeepCount);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await notifications.NotifyAsync("Warning", "Backup", "Second backup copy failed", $"{cfg.SecondaryDirectory}: {e.Message}", ct: ct).ConfigureAwait(false);
                }
            }
            var removed = SqliteBackup.Prune(dir, cfg.KeepCount);
            await db.InTransactionAsync(async s =>
            {
                await BackupHistoryStore.FinishAsync(s, historyId, "Succeeded", info.Path, info.SizeBytes, info.Sha256, null, time.GetUtcNow(), ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "Backup", kind, $"Backup created: {Path.GetFileName(info.Path)} ({info.SizeBytes / 1024.0 / 1024.0:0.0} MB, {info.Manifest.Transactions} transactions){(removed.Count > 0 ? $", {removed.Count} old backup file(s) pruned" : string.Empty)}",
                    reason: info.Sha256, ct: ct).ConfigureAwait(false);
                return 0;
            }, ct).ConfigureAwait(false);
            return info;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Backup failed");
            await db.InTransactionAsync(s => BackupHistoryStore.FinishAsync(s, historyId, "Failed", null, null, null, e.Message, time.GetUtcNow(), ct), ct).ConfigureAwait(false);
            await notifications.NotifyAsync("Error", "Backup", "Backup failed", e.Message, ct: ct).ConfigureAwait(false);
            throw;
        }
    }

    public Task<BackupInfo> VerifyAsync(string path, CancellationToken ct = default) => SqliteBackup.VerifyAsync(path, ct);

    /// <summary>Restores a backup. The current database is kept in a pre-restore folder. Restart the application afterwards.</summary>
    public async Task<string> RestoreAsync(UserContext user, string path, CancellationToken ct = default)
    {
        user.Demand(Permissions.BackupManage);
        var info = await SqliteBackup.VerifyAsync(path, ct).ConfigureAwait(false);
        await audit.WriteAsync(user, "Backup", "RestoreStarted", $"Restoring backup {Path.GetFileName(path)} created {info.Manifest.CreatedUtc:yyyy-MM-dd HH:mm} UTC", reason: info.Sha256, ct: ct).ConfigureAwait(false);
        var kept = await SqliteBackup.RestoreAsync(path, db.Options.SqlitePath!, paths.EvidenceDirectory, time.GetUtcNow(), ct).ConfigureAwait(false);
        await MigrationRunner.MigrateAsync(db, ct: ct).ConfigureAwait(false);
        settings.Invalidate();
        await audit.WriteAsync(user, "Backup", "Restored", $"Database restored from {Path.GetFileName(path)}; previous database kept in {kept}", reason: info.Sha256, ct: ct).ConfigureAwait(false);
        return kept;
    }

    public Task<IReadOnlyList<BackupHistoryEntity>> HistoryAsync(CancellationToken ct = default) => db.ReadAsync(s => BackupHistoryStore.RecentAsync(s, 50, ct), ct);

    /// <summary>Called every minute by the scheduler; runs the daily backup once its time has passed.</summary>
    public async Task<bool> RunScheduledIfDueAsync(CancellationToken ct = default)
    {
        if (db.Options.Provider != DatabaseProvider.Sqlite) return false;
        var cfg = await settings.GetAsync<BackupSettings>(SettingKeys.Backup, ct).ConfigureAwait(false);
        if (!cfg.ScheduleEnabled || !TimeOnly.TryParseExact(cfg.DailyAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return false;
        var localNow = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.Local);
        if (TimeOnly.FromDateTime(localNow.DateTime) < at) return false;
        // Due when no backup (of any kind) has completed since today's scheduled time.
        var scheduledLocal = localNow.Date + at.ToTimeSpan();
        var scheduledUtc = new DateTimeOffset(scheduledLocal, TimeZoneInfo.Local.GetUtcOffset(scheduledLocal));
        var last = await db.ReadAsync(s => BackupHistoryStore.LastSuccessfulAsync(s, ct), ct).ConfigureAwait(false);
        if (last?.CompletedUtc is { } done && done >= scheduledUtc) return false;
        await BackupNowAsync(UserContext.System, "Scheduled", ct).ConfigureAwait(false);
        return true;
    }
}
