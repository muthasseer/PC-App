using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weighbridge.Contracts;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

public sealed class SiteMismatchException(string message) : Exception(message);

/// <summary>
/// Receives data pushed by sites. Every item is versioned: a newer version replaces the stored copy, the same version with
/// the same content is a no-op, an older version is reported stale, and the same version with different content is kept
/// as a conflict for review — nothing is silently overwritten. Each item is processed on its own, so one invalid record
/// never blocks the rest of a batch.
/// </summary>
public sealed class SyncIngestService(Database db, TimeProvider time, ILogger<SyncIngestService> log)
{
    public const int MaxBatch = 500;
    private const long MaxAbsGrams = 1_000_000_000; // 1 000 t: anything larger is not a weighbridge reading

    public async Task<HeartbeatResponse> HeartbeatAsync(SitePrincipal site, HeartbeatRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.SiteCode) && !request.SiteCode.Equals(site.SiteCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new SiteMismatchException($"This site key belongs to site '{site.SiteCode}', but the desktop reports site code '{request.SiteCode}'. Use the key issued for '{request.SiteCode}' or correct the site code.");
        }
        var now = time.GetUtcNow();
        var json = JsonSerializer.Serialize(request, Api.Json);
        return await db.InTransactionAsync(async s =>
        {
            var row = await CloudStore.SiteAsync(s, site.SiteId, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("Unknown site.");
            await s.ExecuteAsync("UPDATE sites SET last_heartbeat_utc = @now, app_version = @AppVersion, heartbeat_json = @json, offline_notified = @notified WHERE id = @SiteId",
                new { now, AppVersion = CloudStore.Clip(request.AppVersion ?? string.Empty, 40), json, notified = false, site.SiteId }, ct).ConfigureAwait(false);
            if (row.OfflineNotified)
            {
                var since = row.LastHeartbeatUtc is { } last ? $" after {FormatDuration(now - last)} without contact" : string.Empty;
                await CloudStore.AddNotificationAsync(s, site.SiteId, Guid.NewGuid(), now, "Info", "Site", "Site back online", $"{row.Name} ({row.Code}) is connected again{since}.",
                    "Site", row.Code, now, ct).ConfigureAwait(false);
            }
            var pending = await s.QuerySingleAsync<long>("SELECT COUNT(*) FROM commands WHERE site_id = @SiteId AND status IN ('Pending', 'Delivered')", new { site.SiteId }, ct).ConfigureAwait(false);
            return new HeartbeatResponse(now, (int)pending);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ transactions

    public async Task<SyncBatchResult> TransactionsAsync(SitePrincipal site, JsonElement body, CancellationToken ct = default)
    {
        var results = new List<SyncItemResult>();
        foreach (var item in Items(body))
        {
            var uid = item.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? string.Empty : string.Empty;
            SyncTransactionDto? dto;
            try { dto = item.Deserialize<SyncTransactionDto>(Api.Json); }
            catch (JsonException e) { results.Add(new SyncItemResult(uid, SyncItemStatus.Rejected, null, $"Invalid transaction: {e.Message}")); continue; }
            if (dto is null) { results.Add(new SyncItemResult(uid, SyncItemStatus.Rejected, null, "Empty transaction.")); continue; }
            if (Validate(dto, site) is { } problem) { results.Add(new SyncItemResult(dto.Uid == Guid.Empty ? uid : dto.Uid.ToString("D"), SyncItemStatus.Rejected, null, problem)); continue; }
            results.Add(await WithRetryAsync(() => StoreTransactionAsync(site, dto, ct)).ConfigureAwait(false));
        }
        return new SyncBatchResult(results);
    }

    internal static string? Validate(SyncTransactionDto t, SitePrincipal site)
    {
        if (t.Uid == Guid.Empty) return "The transaction id is missing.";
        if (string.IsNullOrWhiteSpace(t.TicketNo) || t.TicketNo.Length > 40) return "The ticket number is missing or too long.";
        if (t.Version < 1) return "The version must be 1 or higher.";
        if (string.IsNullOrWhiteSpace(t.VehicleReg) || t.VehicleReg.Length > 40) return "The vehicle registration is missing or too long.";
        if (!Enum.TryParse<TransactionStatus>(t.Status, false, out var status) || !Enum.IsDefined(status) || char.IsDigit(t.Status[0])) return $"Unknown status '{t.Status}'.";
        if (string.IsNullOrEmpty(t.OperatingMode) || char.IsDigit(t.OperatingMode[0]) || !Enum.TryParse<OperatingMode>(t.OperatingMode, false, out _)) return $"Unknown operating mode '{t.OperatingMode}'.";
        if (!string.IsNullOrEmpty(t.SiteCode) && !t.SiteCode.Equals(site.SiteCode, StringComparison.OrdinalIgnoreCase))
        {
            return $"The transaction belongs to site '{t.SiteCode}', not to '{site.SiteCode}' (the site key used).";
        }
        foreach (var g in new[] { t.FirstG, t.SecondG, t.GrossG, t.TareG, t.NetG })
        {
            if (g is { } v && Math.Abs(v) > MaxAbsGrams) return "A weight is outside the plausible range.";
        }
        if (t.GrossG is { } gross && t.TareG is { } tare && t.NetG is { } net && net != gross - tare) return "The net weight does not equal gross minus tare.";
        if (status == TransactionStatus.Completed && t.NetG is null) return "A completed transaction must have a net weight.";
        if (t.Measurements is null || t.Measurements.Count > 50) return "The measurement list is missing or too long.";
        if (t.Measurements.Any(m => m.Uid == Guid.Empty || Math.Abs(m.WeightG) > MaxAbsGrams)) return "A measurement is invalid.";
        return null;
    }

    private async Task<SyncItemResult> StoreTransactionAsync(SitePrincipal site, SyncTransactionDto t, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(t, Api.Json);
        var hash = Sha256(json);
        var uid = t.Uid.ToString("D");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var existing = await s.QuerySingleOrDefaultAsync<ExistingTransactionRow>(
                $"SELECT id, site_id, version, payload_hash, status FROM cloud_transactions WHERE uid = @uid{s.Dialect.ForUpdate}", new { uid }, ct).ConfigureAwait(false);
            var row = new
            {
                site.SiteId, uid, t.TicketNo, t.Version, t.Status, t.VehicleReg, vehicleNorm = VehicleRegistration.Normalize(t.VehicleReg), t.CustomerName, t.SupplierName,
                t.MaterialName, t.GrossG, t.TareG, t.NetG, hasManual = t.HasManualWeight, t.OperatingMode, t.CreatedUtc, t.CompletedUtc, json, hash, now,
            };
            if (existing is null)
            {
                await s.ExecuteAsync("""
                    INSERT INTO cloud_transactions (site_id, uid, ticket_no, version, status, vehicle_reg, vehicle_norm, customer_name, supplier_name, material_name,
                        gross_g, tare_g, net_g, has_manual, operating_mode, created_utc, completed_utc, payload_json, payload_hash, received_utc)
                    VALUES (@SiteId, @uid, @TicketNo, @Version, @Status, @VehicleReg, @vehicleNorm, @CustomerName, @SupplierName, @MaterialName,
                        @GrossG, @TareG, @NetG, @hasManual, @OperatingMode, @CreatedUtc, @CompletedUtc, @json, @hash, @now)
                    """, row, ct).ConfigureAwait(false);
                await NotifyTransitionAsync(s, site, t, null, now, ct).ConfigureAwait(false);
                return new SyncItemResult(uid, SyncItemStatus.Accepted, t.Version, null);
            }
            var e = existing;
            if (e.SiteId != site.SiteId) return new SyncItemResult(uid, SyncItemStatus.Rejected, null, "This transaction id is registered to another site.");
            if (t.Version < e.Version) return new SyncItemResult(uid, SyncItemStatus.Stale, (int)e.Version, $"The cloud already has version {e.Version}.");
            if (t.Version == e.Version)
            {
                if (string.Equals(e.PayloadHash, hash, StringComparison.Ordinal)) return new SyncItemResult(uid, SyncItemStatus.Unchanged, (int)e.Version, null);
                await s.ExecuteAsync("""
                    INSERT INTO sync_conflicts (site_id, entity_type, uid, local_version, server_version, payload_json, at_utc)
                    VALUES (@SiteId, 'Transaction', @uid, @Version, @serverVersion, @json, @now)
                    """, new { site.SiteId, uid, t.Version, serverVersion = e.Version, json, now }, ct).ConfigureAwait(false);
                await CloudStore.AddNotificationAsync(s, site.SiteId, Guid.NewGuid(), now, "Warning", "Sync", "Synchronisation conflict",
                    $"Ticket {t.TicketNo}: the site sent different content for version {t.Version}. The cloud copy was kept; review the conflict.", "Transaction", uid, now, ct).ConfigureAwait(false);
                log.LogWarning("Sync conflict for transaction {Uid} v{Version} from site {Site}", uid, t.Version, site.SiteCode);
                return new SyncItemResult(uid, SyncItemStatus.Conflict, (int)e.Version, "Different content at the same version.");
            }
            await s.ExecuteAsync("""
                UPDATE cloud_transactions SET ticket_no = @TicketNo, version = @Version, status = @Status, vehicle_reg = @VehicleReg, vehicle_norm = @vehicleNorm,
                    customer_name = @CustomerName, supplier_name = @SupplierName, material_name = @MaterialName, gross_g = @GrossG, tare_g = @TareG, net_g = @NetG,
                    has_manual = @hasManual, operating_mode = @OperatingMode, created_utc = @CreatedUtc, completed_utc = @CompletedUtc, payload_json = @json,
                    payload_hash = @hash, received_utc = @now
                WHERE uid = @uid
                """, row, ct).ConfigureAwait(false);
            await NotifyTransitionAsync(s, site, t, e.Status, now, ct).ConfigureAwait(false);
            return new SyncItemResult(uid, SyncItemStatus.Accepted, t.Version, null);
        }, ct).ConfigureAwait(false);
    }

    private static async Task NotifyTransitionAsync(DbSession s, SitePrincipal site, SyncTransactionDto t, string? previousStatus, DateTimeOffset now, CancellationToken ct)
    {
        if (t.Status == previousStatus) return;
        var uid = t.Uid.ToString("D");
        if (t.Status == nameof(TransactionStatus.Completed))
        {
            var net = WeightDisplayOptions.DefaultKg.Format(Weight.FromGrams(t.NetG ?? 0));
            var flags = new List<string>();
            if (t.HasManualWeight) flags.Add("contains a MANUAL weight");
            if (t.HasOverride) flags.Add("unstable-weight override used");
            var message = $"Ticket {t.TicketNo} · {t.VehicleReg}{(t.MaterialName is null ? string.Empty : " · " + t.MaterialName)} · net {net}{(flags.Count > 0 ? " · " + string.Join(", ", flags) : string.Empty)}";
            await CloudStore.AddNotificationAsync(s, site.SiteId, Guid.NewGuid(), t.CompletedUtc ?? now, flags.Count > 0 ? "Warning" : "Info", "Weighing", "Weighment completed", message,
                "Transaction", uid, now, ct).ConfigureAwait(false);
        }
        else if (t.Status == nameof(TransactionStatus.Cancelled))
        {
            await CloudStore.AddNotificationAsync(s, site.SiteId, Guid.NewGuid(), t.CancelledUtc ?? now, "Warning", "Weighing", "Ticket cancelled",
                $"Ticket {t.TicketNo} · {t.VehicleReg} cancelled by {t.CancelledBy ?? "?"}: {t.CancelReason}", "Transaction", uid, now, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ notifications, audit, users

    public async Task<SyncBatchResult> NotificationsAsync(SitePrincipal site, SyncBatch<SyncNotificationDto> batch, CancellationToken ct = default)
    {
        var results = new List<SyncItemResult>();
        var now = time.GetUtcNow();
        foreach (var n in Limit(batch.Items))
        {
            var uid = n.Uid.ToString("D");
            if (n.Uid == Guid.Empty || string.IsNullOrWhiteSpace(n.Title) || string.IsNullOrWhiteSpace(n.Severity) || n.Severity.Length > 20)
            {
                results.Add(new SyncItemResult(uid, SyncItemStatus.Rejected, null, "Invalid notification."));
                continue;
            }
            results.Add(await WithRetryAsync(() => db.InTransactionAsync(async s =>
            {
                var exists = await s.QuerySingleOrDefaultAsync<long?>("SELECT id FROM cloud_notifications WHERE uid = @uid", new { uid }, ct).ConfigureAwait(false);
                if (exists is not null) return new SyncItemResult(uid, SyncItemStatus.Unchanged, 1, null);
                await CloudStore.AddNotificationAsync(s, site.SiteId, n.Uid, n.AtUtc, n.Severity, CloudStore.Clip(n.Category ?? "General", 40), n.Title, n.Message ?? string.Empty,
                    n.EntityType, n.EntityId, now, ct).ConfigureAwait(false);
                return new SyncItemResult(uid, SyncItemStatus.Accepted, 1, null);
            }, ct)).ConfigureAwait(false));
        }
        return new SyncBatchResult(results);
    }

    public async Task<SyncBatchResult> AuditAsync(SitePrincipal site, SyncBatch<SyncAuditEventDto> batch, CancellationToken ct = default)
    {
        var results = new List<SyncItemResult>();
        var now = time.GetUtcNow();
        foreach (var e in Limit(batch.Items))
        {
            var uid = e.Uid.ToString("D");
            if (e.Uid == Guid.Empty || !IsHash(e.Hash) || !IsHash(e.PrevHash) && e.PrevHash != Genesis || string.IsNullOrWhiteSpace(e.Category) || string.IsNullOrWhiteSpace(e.Action))
            {
                results.Add(new SyncItemResult(uid, SyncItemStatus.Rejected, null, "Invalid audit event."));
                continue;
            }
            results.Add(await WithRetryAsync(() => db.InTransactionAsync(async s =>
            {
                var x = await s.QuerySingleOrDefaultAsync<ExistingAuditRow>("SELECT site_id, hash FROM cloud_audit_events WHERE uid = @uid", new { uid }, ct).ConfigureAwait(false);
                if (x is not null)
                {
                    if (x.SiteId == site.SiteId && x.Hash == e.Hash) return new SyncItemResult(uid, SyncItemStatus.Unchanged, 1, null);
                    await s.ExecuteAsync("""
                        INSERT INTO sync_conflicts (site_id, entity_type, uid, local_version, server_version, payload_json, at_utc)
                        VALUES (@SiteId, 'AuditEvent', @uid, 1, 1, @json, @now)
                        """, new { site.SiteId, uid, json = JsonSerializer.Serialize(e, Api.Json), now }, ct).ConfigureAwait(false);
                    await CloudStore.AddNotificationAsync(s, site.SiteId, Guid.NewGuid(), now, "Critical", "Audit", "Audit event altered",
                        $"Audit event {e.LocalId} from site {site.SiteCode} was received again with a different hash. The original copy was kept.", "AuditEvent", uid, now, ct).ConfigureAwait(false);
                    return new SyncItemResult(uid, SyncItemStatus.Conflict, 1, "An audit event with this id but different content already exists.");
                }
                await s.ExecuteAsync("""
                    INSERT INTO cloud_audit_events (site_id, uid, local_id, at_utc, username, machine, category, action, entity_type, entity_id, summary, reason, prev_hash, hash, received_utc)
                    VALUES (@SiteId, @uid, @LocalId, @AtUtc, @Username, @Machine, @Category, @Action, @EntityType, @EntityId, @Summary, @Reason, @PrevHash, @Hash, @now)
                    """, new
                {
                    site.SiteId, uid, e.LocalId, e.AtUtc, Username = e.Username ?? string.Empty, e.Machine, e.Category, e.Action, e.EntityType, e.EntityId,
                    Summary = CloudStore.Clip(e.Summary ?? string.Empty, 4000), e.Reason, e.PrevHash, e.Hash, now,
                }, ct).ConfigureAwait(false);
                return new SyncItemResult(uid, SyncItemStatus.Accepted, 1, null);
            }, ct)).ConfigureAwait(false));
        }
        return new SyncBatchResult(results);
    }

    /// <summary>Replaces the site's user directory (names and roles only; sites never send password hashes).</summary>
    public async Task<SyncBatchResult> UsersAsync(SitePrincipal site, SyncBatch<SiteUserDto> batch, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var users = Limit(batch.Items).Where(u => !string.IsNullOrWhiteSpace(u.Username)).GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        await db.InTransactionAsync(async s =>
        {
            await s.ExecuteAsync("DELETE FROM site_users WHERE site_id = @SiteId", new { site.SiteId }, ct).ConfigureAwait(false);
            foreach (var u in users)
            {
                await s.ExecuteAsync("""
                    INSERT INTO site_users (site_id, username, display_name, role, is_active, last_login_utc, updated_utc)
                    VALUES (@SiteId, @Username, @DisplayName, @Role, @IsActive, @LastLoginUtc, @now)
                    """, new
                {
                    site.SiteId, Username = CloudStore.Clip(u.Username, 100), DisplayName = CloudStore.Clip(u.DisplayName ?? u.Username, 200), Role = CloudStore.Clip(u.Role ?? string.Empty, 40),
                    u.IsActive, u.LastLoginUtc, now,
                }, ct).ConfigureAwait(false);
            }
            return 0;
        }, ct).ConfigureAwait(false);
        return new SyncBatchResult(users.Select(u => new SyncItemResult(u.Username, SyncItemStatus.Accepted, null, null)).ToList());
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Pending commands for the site. They stay deliverable until acknowledged, so a lost response is retried.</summary>
    public Task<List<RemoteCommandDto>> CommandsAsync(SitePrincipal site, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.InTransactionAsync(async s =>
        {
            var rows = (await s.QueryAsync<CommandRow>("""
                SELECT id, uid, site_id, type, target_uid, reason, requested_by, requested_utc, status, result_message, delivered_utc, completed_utc
                FROM commands WHERE site_id = @SiteId AND status IN ('Pending', 'Delivered') ORDER BY id LIMIT 50
                """, new { site.SiteId }, ct).ConfigureAwait(false)).ToList();
            foreach (var r in rows.Where(r => r.Status == "Pending"))
            {
                await s.ExecuteAsync("UPDATE commands SET status = 'Delivered', delivered_utc = @now WHERE id = @Id", new { now, r.Id }, ct).ConfigureAwait(false);
                r.Status = "Delivered";
            }
            return rows.Select(r => r.ToDto()).ToList();
        }, ct);
    }

    public Task<bool> AckCommandAsync(SitePrincipal site, long id, CommandAck ack, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.InTransactionAsync(async s =>
        {
            var c = await s.QuerySingleOrDefaultAsync<CommandRow>($"""
                SELECT id, uid, site_id, type, target_uid, reason, requested_by, requested_utc, status, result_message, delivered_utc, completed_utc
                FROM commands WHERE id = @id AND site_id = @SiteId{s.Dialect.ForUpdate}
                """, new { id, site.SiteId }, ct).ConfigureAwait(false);
            if (c is null) return false;
            if (c.Status is "Completed" or "Failed") return true;
            var status = ack.Success ? "Completed" : "Failed";
            var message = CloudStore.Clip(ack.Message ?? string.Empty, 1000);
            await s.ExecuteAsync("UPDATE commands SET status = @status, result_message = @message, completed_utc = @now WHERE id = @id", new { status, message, now, id }, ct).ConfigureAwait(false);
            await CloudStore.AddNotificationAsync(s, site.SiteId, Guid.NewGuid(), now, ack.Success ? "Info" : "Warning", "Command",
                ack.Success ? "Remote request completed" : "Remote request failed", $"{c.Type} requested by {c.RequestedBy}: {message}", "Transaction", c.TargetUid, now, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, $"site:{site.SiteCode}", "CommandAcknowledged", $"Command {c.Id} {c.Type} {status}: {message}", null, ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The "previous hash" of a site's very first audit event.</summary>
    public const string Genesis = "GENESIS";

    private static bool IsHash(string? h) => h is { Length: 64 } && h.All(Uri.IsHexDigit);

    private static IEnumerable<T> Limit<T>(IReadOnlyList<T>? items) => (items ?? []).Take(MaxBatch);

    private static IEnumerable<JsonElement> Items(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Take(MaxBatch)
            : throw new ArgumentException("The body must be an object with an 'items' array.");

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> work)
    {
        try { return await work().ConfigureAwait(false); }
        catch (Exception e) when (CloudStore.IsUniqueViolation(e))
        {
            // A parallel request inserted the same record first; the second attempt sees it and compares versions.
            return await work().ConfigureAwait(false);
        }
    }

    private static string FormatDuration(TimeSpan d) => d.TotalHours >= 1
        ? $"{(int)d.TotalHours} h {d.Minutes} min"
        : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min";
}
