using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

/// <summary>
/// Raises one "site offline" notification when a site stops sending heartbeats (the matching "back online" notification is
/// raised by the next heartbeat) and removes expired refresh tokens.
/// </summary>
public sealed class SiteMonitor(Database db, CloudOptions options, TimeProvider time, ILogger<SiteMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        do
        {
            try { await CheckAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Site monitor check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> CheckAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var limit = now - TimeSpan.FromMinutes(options.SiteOfflineAfterMinutes);
        return await db.InTransactionAsync(async s =>
        {
            var silent = (await s.QueryAsync<SiteRow>($"""
                SELECT id, code, name, key_hash, key_prefix, is_active, created_utc, last_heartbeat_utc, app_version, heartbeat_json, offline_notified
                FROM sites WHERE is_active = @active AND offline_notified = @notified AND last_heartbeat_utc IS NOT NULL AND last_heartbeat_utc < @limit{s.Dialect.ForUpdate}
                """, new { active = true, notified = false, limit = Iso.ToText(limit) }, ct).ConfigureAwait(false)).ToList();
            foreach (var site in silent)
            {
                await s.ExecuteAsync("UPDATE sites SET offline_notified = @notified WHERE id = @Id", new { notified = true, site.Id }, ct).ConfigureAwait(false);
                await CloudStore.AddNotificationAsync(s, site.Id, Guid.NewGuid(), now, "Warning", "Site", "Site offline",
                    $"No contact with {site.Name} ({site.Code}) since {site.LastHeartbeatUtc:yyyy-MM-dd HH:mm} UTC. Weighing at the site continues offline; its data is uploaded when the connection returns.",
                    "Site", site.Code, now, ct).ConfigureAwait(false);
                log.LogWarning("Site {Site} is offline (last heartbeat {Last:o})", site.Code, site.LastHeartbeatUtc);
            }
            await s.ExecuteAsync("DELETE FROM refresh_tokens WHERE expires_utc < @cutoff", new { cutoff = Iso.ToText(now.AddDays(-7)) }, ct).ConfigureAwait(false);
            return silent.Count;
        }, ct).ConfigureAwait(false);
    }
}
