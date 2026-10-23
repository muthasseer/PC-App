using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application;

/// <summary>Writes audit events (inside the caller's transaction, so the event and the change commit together).</summary>
public sealed class AuditService(Database db, TimeProvider time, ILogger<AuditService> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static string? ToJson(object? value) => value is null ? null : JsonSerializer.Serialize(value, Json);

    public async Task WriteAsync(DbSession s, UserContext user, string category, string action, string summary, string? entityType = null, string? entityId = null,
        string? oldJson = null, string? newJson = null, string? reason = null, CancellationToken ct = default)
    {
        await AuditStore.AppendAsync(s, new AuditEntry(category, action, summary, user.UserId == 0 ? null : user.UserId, user.Username, entityType, entityId, oldJson, newJson, reason),
            time.GetUtcNow(), ct).ConfigureAwait(false);
        log.LogInformation("AUDIT {Category}/{Action} by {User}: {Summary}", category, action, user.Username, summary);
    }

    /// <summary>Stand-alone audit event in its own transaction.</summary>
    public Task WriteAsync(UserContext user, string category, string action, string summary, string? entityType = null, string? entityId = null,
        string? oldJson = null, string? newJson = null, string? reason = null, CancellationToken ct = default) =>
        db.InTransactionAsync(async s =>
        {
            await WriteAsync(s, user, category, action, summary, entityType, entityId, oldJson, newJson, reason, ct).ConfigureAwait(false);
            return 0;
        }, ct);

    public Task<AuditVerification> VerifyAsync(CancellationToken ct = default) => db.ReadAsync(s => AuditStore.VerifyAsync(s, ct), ct);

    public Task<PagedResult<AuditEventEntity>> QueryAsync(UserContext user, AuditFilter filter, CancellationToken ct = default)
    {
        user.Demand(Permissions.AuditView);
        return db.ReadAsync(s => AuditStore.QueryAsync(s, filter, ct), ct);
    }
}

public sealed class NotificationService(Database db, TimeProvider time)
{
    /// <summary>Raised after a notification is stored (UI toasts). Raised on the caller's thread.</summary>
    public event EventHandler<NotificationEntity>? Raised;

    public async Task NotifyAsync(string severity, string category, string title, string message, string? entityType = null, string? entityId = null, CancellationToken ct = default)
    {
        var n = new NotificationEntity { AtUtc = time.GetUtcNow(), Severity = severity, Category = category, Title = title, Message = message, EntityType = entityType, EntityId = entityId };
        await db.InTransactionAsync(async s =>
        {
            await NotificationStore.AddAsync(s, n, ct).ConfigureAwait(false);
            await SyncQueueStore.EnqueueAsync(s, "Notification", n.Uid.ToString("D"), 1, ToJson(n), n.AtUtc, ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
        Raised?.Invoke(this, n);
    }

    public Task<IReadOnlyList<NotificationEntity>> RecentAsync(int limit = 100, CancellationToken ct = default) => db.ReadAsync(s => NotificationStore.RecentAsync(s, limit, false, ct), ct);
    public Task<long> UnreadCountAsync(CancellationToken ct = default) => db.ReadAsync(s => NotificationStore.UnreadCountAsync(s, ct), ct);
    public Task MarkAllReadAsync(CancellationToken ct = default) => db.ReadAsync(s => NotificationStore.MarkAllReadAsync(s, ct), ct);

    private static string ToJson(NotificationEntity n) => JsonSerializer.Serialize(
        new Contracts.SyncNotificationDto(n.Uid, n.AtUtc, n.Severity, n.Category, n.Title, n.Message, n.EntityType, n.EntityId), Contracts.Api.Json);
}
