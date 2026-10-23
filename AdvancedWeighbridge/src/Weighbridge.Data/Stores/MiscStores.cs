using System.Text.Json;

namespace Weighbridge.Data.Stores;

public static class SettingsStore
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static async Task<T?> GetAsync<T>(DbSession s, string key, CancellationToken ct = default)
    {
        var json = await s.QuerySingleOrDefaultAsync<string>("SELECT value_json FROM settings WHERE key = @key", new { key }, ct).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, Json);
    }

    public static Task<string?> GetRawAsync(DbSession s, string key, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<string>("SELECT value_json FROM settings WHERE key = @key", new { key }, ct);

    public static Task SetAsync<T>(DbSession s, string key, T value, long? userId, DateTimeOffset at, bool isSecret = false, CancellationToken ct = default) =>
        SetRawAsync(s, key, JsonSerializer.Serialize(value, Json), userId, at, isSecret, ct);

    public static Task SetRawAsync(DbSession s, string key, string json, long? userId, DateTimeOffset at, bool isSecret = false, CancellationToken ct = default) =>
        s.ExecuteAsync("""
            INSERT INTO settings (key, value_json, is_secret, updated_utc, updated_by) VALUES (@key, @json, @isSecret, @at, @userId)
            ON CONFLICT (key) DO UPDATE SET value_json = excluded.value_json, is_secret = excluded.is_secret, updated_utc = excluded.updated_utc, updated_by = excluded.updated_by
            """, new { key, json, isSecret, at, userId }, ct);

    public static async Task<IReadOnlyList<(string Key, string ValueJson, bool IsSecret)>> AllAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<(string, string, bool)>("SELECT key, value_json, is_secret FROM settings ORDER BY key", ct: ct).ConfigureAwait(false)).ToList();
}

public static class SyncQueueStore
{
    /// <summary>Queues the latest state of an entity. A pending item for the same entity is replaced (only the newest version matters).</summary>
    public static async Task EnqueueAsync(DbSession s, string entityType, string entityUid, int version, string payloadJson, DateTimeOffset now, CancellationToken ct = default)
    {
        var updated = await s.ExecuteAsync("""
            UPDATE sync_queue SET payload_json = @payloadJson, entity_version = @version, next_attempt_utc = @now, attempts = 0, last_error = NULL
            WHERE entity_type = @entityType AND entity_uid = @entityUid AND status IN ('Pending', 'Failed')
            """, new { entityType, entityUid, version, payloadJson, now }, ct).ConfigureAwait(false);
        if (updated == 0)
        {
            await s.ExecuteAsync("""
                INSERT INTO sync_queue (entity_type, entity_uid, entity_version, payload_json, created_utc, attempts, next_attempt_utc, status)
                VALUES (@entityType, @entityUid, @version, @payloadJson, @now, 0, @now, 'Pending')
                """, new { entityType, entityUid, version, payloadJson, now }, ct).ConfigureAwait(false);
        }
    }

    public static async Task<IReadOnlyList<SyncQueueEntity>> DueAsync(DbSession s, DateTimeOffset now, int limit, CancellationToken ct = default) =>
        (await s.QueryAsync<SyncQueueEntity>("""
            SELECT id, entity_type, entity_uid, entity_version, payload_json, created_utc, attempts, next_attempt_utc, last_error, status, sent_utc
            FROM sync_queue WHERE status IN ('Pending', 'Failed') AND next_attempt_utc <= @now ORDER BY id LIMIT @limit
            """, new { now = Iso.ToText(now), limit }, ct).ConfigureAwait(false)).ToList();

    // The outcome of an upload only applies while the queue row still holds the version that was uploaded: a change
    // made during the upload replaces the row's payload (see EnqueueAsync) and must still be sent on the next run.

    /// <summary>Marks the item as sent. Returns false when a newer version was queued meanwhile (it stays pending).</summary>
    public static async Task<bool> MarkSentAsync(DbSession s, SyncQueueEntity item, DateTimeOffset at, CancellationToken ct = default) =>
        await s.ExecuteAsync("UPDATE sync_queue SET status = 'Sent', sent_utc = @at, last_error = NULL WHERE id = @Id AND entity_version = @EntityVersion",
            new { item.Id, item.EntityVersion, at }, ct).ConfigureAwait(false) == 1;

    public static Task<int> MarkFailedAsync(DbSession s, SyncQueueEntity item, string error, DateTimeOffset nextAttempt, CancellationToken ct = default) =>
        s.ExecuteAsync("""
            UPDATE sync_queue SET status = 'Failed', attempts = attempts + 1, last_error = @error, next_attempt_utc = @nextAttempt
            WHERE id = @Id AND entity_version = @EntityVersion
            """, new { item.Id, item.EntityVersion, error = error.Length > 1000 ? error[..1000] : error, nextAttempt }, ct);

    /// <summary>Records a conflict reported for this version. Ignored when a newer local version is already queued (it supersedes it).</summary>
    public static async Task<bool> MarkConflictAsync(DbSession s, SyncQueueEntity item, int? remoteVersion, string detail, DateTimeOffset at, CancellationToken ct = default)
    {
        var updated = await s.ExecuteAsync("UPDATE sync_queue SET status = 'Conflict', last_error = @detail WHERE id = @Id AND entity_version = @EntityVersion",
            new { item.Id, item.EntityVersion, detail }, ct).ConfigureAwait(false);
        if (updated == 0) return false;
        await s.ExecuteAsync("""
            INSERT INTO sync_conflicts (entity_type, entity_uid, local_version, remote_version, detail, at_utc, resolved)
            VALUES (@EntityType, @EntityUid, @EntityVersion, @remoteVersion, @detail, @at, @resolved)
            """, new { item.EntityType, item.EntityUid, item.EntityVersion, remoteVersion, detail, at, resolved = false }, ct).ConfigureAwait(false);
        return true;
    }

    public sealed record Counts(long Pending, long Failed, long Conflicts, long Sent, DateTimeOffset? OldestPending);

    public static async Task<Counts> CountsAsync(DbSession s, CancellationToken ct = default)
    {
        var rows = (await s.QueryAsync<(string Status, long N)>("SELECT status, COUNT(*) FROM sync_queue GROUP BY status", ct: ct).ConfigureAwait(false)).ToDictionary(r => r.Status, r => r.N);
        var oldest = await s.QuerySingleOrDefaultAsync<string>("SELECT MIN(created_utc) FROM sync_queue WHERE status IN ('Pending', 'Failed')", ct: ct).ConfigureAwait(false);
        return new Counts(rows.GetValueOrDefault("Pending"), rows.GetValueOrDefault("Failed"), rows.GetValueOrDefault("Conflict"), rows.GetValueOrDefault("Sent"),
            oldest is null ? null : Iso.Parse(oldest));
    }

    /// <summary>Sent items are only bookkeeping; the synchronised records themselves stay in their tables.</summary>
    public static Task<int> PurgeSentAsync(DbSession s, DateTimeOffset olderThan, CancellationToken ct = default) =>
        s.ExecuteAsync("DELETE FROM sync_queue WHERE status = 'Sent' AND sent_utc < @olderThan", new { olderThan = Iso.ToText(olderThan) }, ct);
}

public static class NotificationStore
{
    public static Task<int> AddAsync(DbSession s, NotificationEntity n, CancellationToken ct = default) => s.ExecuteAsync("""
        INSERT INTO notifications (uid, at_utc, severity, category, title, message, entity_type, entity_id, is_read)
        VALUES (@Uid, @AtUtc, @Severity, @Category, @Title, @Message, @EntityType, @EntityId, @IsRead)
        """, n, ct);

    public static async Task<IReadOnlyList<NotificationEntity>> RecentAsync(DbSession s, int limit = 100, bool unreadOnly = false, CancellationToken ct = default) =>
        (await s.QueryAsync<NotificationEntity>($"""
            SELECT id, uid, at_utc, severity, category, title, message, entity_type, entity_id, is_read FROM notifications
            {(unreadOnly ? "WHERE is_read = @no" : "")} ORDER BY id DESC LIMIT @limit
            """, new { limit, no = false }, ct).ConfigureAwait(false)).ToList();

    public static Task<long> UnreadCountAsync(DbSession s, CancellationToken ct = default) =>
        s.QuerySingleAsync<long>("SELECT COUNT(*) FROM notifications WHERE is_read = @no", new { no = false }, ct);

    public static Task<int> MarkReadAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE notifications SET is_read = @yes WHERE id = @id", new { id, yes = true }, ct);

    public static Task<int> MarkAllReadAsync(DbSession s, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE notifications SET is_read = @yes WHERE is_read = @no", new { yes = true, no = false }, ct);
}

public static class DeviceEventStore
{
    public static Task<int> AddAsync(DbSession s, DeviceEventEntity e, CancellationToken ct = default) => s.ExecuteAsync(
        "INSERT INTO device_events (at_utc, weighbridge_id, device, event_type, message) VALUES (@AtUtc, @WeighbridgeId, @Device, @EventType, @Message)", e, ct);

    public static async Task<IReadOnlyList<DeviceEventEntity>> QueryAsync(DbSession s, DateTimeOffset from, DateTimeOffset to, string? eventType = null, int limit = 10_000, CancellationToken ct = default) =>
        (await s.QueryAsync<DeviceEventEntity>($"""
            SELECT id, at_utc, weighbridge_id, device, event_type, message FROM device_events
            WHERE at_utc >= @from AND at_utc < @to {(eventType is null ? "" : "AND event_type = @eventType")} ORDER BY id DESC LIMIT @limit
            """, new { from = Iso.ToText(from), to = Iso.ToText(to), eventType, limit }, ct).ConfigureAwait(false)).ToList();
}

public static class BackupHistoryStore
{
    public static Task<long> StartAsync(DbSession s, BackupHistoryEntity b, CancellationToken ct = default) => s.ExecuteScalarAsync<long>(
        "INSERT INTO backup_history (started_utc, kind, file_path, status, initiated_by) VALUES (@StartedUtc, @Kind, @FilePath, @Status, @InitiatedBy) RETURNING id", b, ct)!;

    public static Task<int> FinishAsync(DbSession s, long id, string status, string? path, long? size, string? sha256, string? error, DateTimeOffset at, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE backup_history SET status = @status, file_path = @path, size_bytes = @size, sha256 = @sha256, error = @error, completed_utc = @at WHERE id = @id",
            new { id, status, path, size, sha256, error, at }, ct);

    public static async Task<IReadOnlyList<BackupHistoryEntity>> RecentAsync(DbSession s, int limit = 50, CancellationToken ct = default) =>
        (await s.QueryAsync<BackupHistoryEntity>("SELECT id, started_utc, completed_utc, kind, file_path, size_bytes, sha256, status, error, initiated_by FROM backup_history ORDER BY id DESC LIMIT @limit",
            new { limit }, ct).ConfigureAwait(false)).ToList();

    public static Task<BackupHistoryEntity?> LastSuccessfulAsync(DbSession s, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<BackupHistoryEntity>("SELECT id, started_utc, completed_utc, kind, file_path, size_bytes, sha256, status, error, initiated_by FROM backup_history WHERE status = 'Succeeded' ORDER BY id DESC LIMIT 1", ct: ct);
}

public static class ApprovalStore
{
    private const string Columns = """
        a.id, a.uid, a.request_type, a.entity_type, a.entity_id, a.requested_by, r.display_name AS requested_by_name, a.requested_utc, a.status,
        a.decided_by, d.display_name AS decided_by_name, a.decided_utc, a.decision_reason, a.payload_json, a.source
        """;

    public static Task<long> AddAsync(DbSession s, ApprovalEntity a, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO approvals (uid, request_type, entity_type, entity_id, requested_by, requested_utc, status, decided_by, decided_utc, decision_reason, payload_json, source)
        VALUES (@Uid, @RequestType, @EntityType, @EntityId, @RequestedBy, @RequestedUtc, @Status, @DecidedBy, @DecidedUtc, @DecisionReason, @PayloadJson, @Source) RETURNING id
        """, a, ct)!;

    public static Task<int> DecideAsync(DbSession s, long id, bool approved, long decidedBy, string? reason, DateTimeOffset at, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE approvals SET status = @status, decided_by = @decidedBy, decided_utc = @at, decision_reason = @reason WHERE id = @id AND status = 'Pending'
        """, new { id, status = approved ? "Approved" : "Rejected", decidedBy, reason, at }, ct);

    public static async Task<IReadOnlyList<ApprovalEntity>> PendingAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<ApprovalEntity>($"SELECT {Columns} FROM approvals a JOIN users r ON r.id = a.requested_by LEFT JOIN users d ON d.id = a.decided_by WHERE a.status = 'Pending' ORDER BY a.id", ct: ct).ConfigureAwait(false)).ToList();

    public static async Task<IReadOnlyList<ApprovalEntity>> ForEntityAsync(DbSession s, string entityType, string entityId, CancellationToken ct = default) =>
        (await s.QueryAsync<ApprovalEntity>($"SELECT {Columns} FROM approvals a JOIN users r ON r.id = a.requested_by LEFT JOIN users d ON d.id = a.decided_by WHERE a.entity_type = @entityType AND a.entity_id = @entityId ORDER BY a.id",
            new { entityType, entityId }, ct).ConfigureAwait(false)).ToList();

    public static Task<ApprovalEntity?> GetAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<ApprovalEntity>($"SELECT {Columns} FROM approvals a JOIN users r ON r.id = a.requested_by LEFT JOIN users d ON d.id = a.decided_by WHERE a.id = @id", new { id }, ct);
}

public static class TicketTemplateStore
{
    public static async Task<IReadOnlyList<TicketTemplateEntity>> AllAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<TicketTemplateEntity>("SELECT id, name, paper, template_json, is_default, updated_utc FROM ticket_templates ORDER BY name", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<TicketTemplateEntity?> DefaultAsync(DbSession s, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TicketTemplateEntity>("SELECT id, name, paper, template_json, is_default, updated_utc FROM ticket_templates ORDER BY is_default DESC, id LIMIT 1", ct: ct);

    public static async Task<long> SaveAsync(DbSession s, TicketTemplateEntity t, CancellationToken ct = default)
    {
        if (t.IsDefault) await s.ExecuteAsync("UPDATE ticket_templates SET is_default = @no", new { no = false }, ct).ConfigureAwait(false);
        if (t.Id == 0)
        {
            return await s.ExecuteScalarAsync<long>("INSERT INTO ticket_templates (name, paper, template_json, is_default, updated_utc) VALUES (@Name, @Paper, @TemplateJson, @IsDefault, @UpdatedUtc) RETURNING id", t, ct).ConfigureAwait(false);
        }
        await s.ExecuteAsync("UPDATE ticket_templates SET name = @Name, paper = @Paper, template_json = @TemplateJson, is_default = @IsDefault, updated_utc = @UpdatedUtc WHERE id = @Id", t, ct).ConfigureAwait(false);
        return t.Id;
    }
}
