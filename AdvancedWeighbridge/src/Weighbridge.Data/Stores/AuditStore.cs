using System.Security.Cryptography;
using System.Text;

namespace Weighbridge.Data.Stores;

public sealed record AuditEntry(
    string Category,
    string Action,
    string Summary,
    long? UserId,
    string Username,
    string? EntityType = null,
    string? EntityId = null,
    string? OldJson = null,
    string? NewJson = null,
    string? Reason = null,
    string? Machine = null);

public sealed record AuditVerification(bool Valid, long Checked, long? FirstInvalidId, string Message);

public sealed record AuditFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Category { get; init; }
    public string? Username { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? Text { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
}

/// <summary>
/// Append-only, hash-chained audit log. Each event stores SHA-256(previous hash ‖ canonical fields); the chain head
/// row serialises writers. <see cref="VerifyAsync"/> detects any edited, removed or inserted event.
/// </summary>
public static class AuditStore
{
    public const string Genesis = "GENESIS";

    public static async Task<AuditEventEntity> AppendAsync(DbSession s, AuditEntry entry, DateTimeOffset at, CancellationToken ct = default)
    {
        if (s.Transaction is null) throw new InvalidOperationException("Audit events must be written inside a transaction.");
        var head = await s.QuerySingleAsync<(long LastId, string LastHash)>($"SELECT last_id, last_hash FROM audit_chain WHERE id = 1{s.Dialect.ForUpdate}", ct: ct).ConfigureAwait(false);
        var e = new AuditEventEntity
        {
            Uid = Guid.NewGuid(),
            AtUtc = DateTimeOffset.Parse(Iso.ToText(at), System.Globalization.CultureInfo.InvariantCulture),
            UserId = entry.UserId,
            Username = entry.Username,
            Machine = entry.Machine ?? Environment.MachineName,
            Category = entry.Category,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            Summary = entry.Summary,
            OldJson = entry.OldJson,
            NewJson = entry.NewJson,
            Reason = entry.Reason,
            PrevHash = head.LastHash,
        };
        e.Hash = ComputeHash(e);
        e.Id = await s.ExecuteScalarAsync<long>("""
            INSERT INTO audit_events (uid, at_utc, user_id, username, machine, category, action, entity_type, entity_id, summary, old_json, new_json, reason, prev_hash, hash)
            VALUES (@Uid, @AtUtc, @UserId, @Username, @Machine, @Category, @Action, @EntityType, @EntityId, @Summary, @OldJson, @NewJson, @Reason, @PrevHash, @Hash)
            RETURNING id
            """, e, ct).ConfigureAwait(false);
        await s.ExecuteAsync("UPDATE audit_chain SET last_id = @Id, last_hash = @Hash WHERE id = 1", new { e.Id, e.Hash }, ct).ConfigureAwait(false);
        return e;
    }

    public static string ComputeHash(AuditEventEntity e)
    {
        var canonical = string.Join('\u001F',
            e.Uid.ToString("D"), Iso.ToText(e.AtUtc), e.UserId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, e.Username,
            e.Machine ?? string.Empty, e.Category, e.Action, e.EntityType ?? string.Empty, e.EntityId ?? string.Empty, e.Summary,
            e.OldJson ?? string.Empty, e.NewJson ?? string.Empty, e.Reason ?? string.Empty, e.PrevHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static async Task<AuditVerification> VerifyAsync(DbSession s, CancellationToken ct = default)
    {
        var expectedPrev = Genesis;
        long checkedCount = 0;
        long lastId = 0;
        const int chunk = 5000;
        while (true)
        {
            var rows = (await s.QueryAsync<AuditEventEntity>($"{Select} WHERE id > @lastId ORDER BY id LIMIT {chunk}", new { lastId }, ct).ConfigureAwait(false)).ToList();
            foreach (var e in rows)
            {
                if (e.PrevHash != expectedPrev) return new AuditVerification(false, checkedCount, e.Id, $"Event {e.Id}: chain broken (an earlier event was removed or altered).");
                if (ComputeHash(e) != e.Hash) return new AuditVerification(false, checkedCount, e.Id, $"Event {e.Id}: content does not match its hash (the event was altered).");
                expectedPrev = e.Hash;
                lastId = e.Id;
                checkedCount++;
            }
            if (rows.Count < chunk) break;
        }
        var head = await s.QuerySingleAsync<(long LastId, string LastHash)>("SELECT last_id, last_hash FROM audit_chain WHERE id = 1", ct: ct).ConfigureAwait(false);
        if (head.LastId != lastId || head.LastHash != expectedPrev)
        {
            return new AuditVerification(false, checkedCount, null, "The newest audit events are missing (chain head does not match).");
        }
        return new AuditVerification(true, checkedCount, null, $"{checkedCount} audit events verified.");
    }

    private const string Select = "SELECT id, uid, at_utc, user_id, username, machine, category, action, entity_type, entity_id, summary, old_json, new_json, reason, prev_hash, hash FROM audit_events";

    public static async Task<PagedResult<AuditEventEntity>> QueryAsync(DbSession s, AuditFilter f, CancellationToken ct = default)
    {
        var w = new List<string>();
        var p = new Dapper.DynamicParameters();
        if (f.From is { } from) { w.Add("at_utc >= @from"); p.Add("from", Iso.ToText(from)); }
        if (f.To is { } to) { w.Add("at_utc < @to"); p.Add("to", Iso.ToText(to)); }
        if (!string.IsNullOrWhiteSpace(f.Category)) { w.Add("category = @category"); p.Add("category", f.Category); }
        if (!string.IsNullOrWhiteSpace(f.Username)) { w.Add("lower(username) = lower(@username)"); p.Add("username", f.Username); }
        if (!string.IsNullOrWhiteSpace(f.EntityType)) { w.Add("entity_type = @entityType"); p.Add("entityType", f.EntityType); }
        if (!string.IsNullOrWhiteSpace(f.EntityId)) { w.Add("entity_id = @entityId"); p.Add("entityId", f.EntityId); }
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            w.Add($"({s.Dialect.ContainsCi("summary", "@text")} OR {s.Dialect.ContainsCi("action", "@text")} OR {s.Dialect.ContainsCi("COALESCE(reason, '')", "@text")})");
            p.Add("text", SqlDialect.LikePattern(f.Text));
        }
        var where = w.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", w);
        var pageSize = Math.Clamp(f.PageSize, 1, 10_000);
        var page = Math.Max(1, f.Page);
        p.Add("limit", pageSize);
        p.Add("offset", (page - 1) * pageSize);
        var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) FROM audit_events {where}", p, ct).ConfigureAwait(false);
        var items = await s.QueryAsync<AuditEventEntity>($"{Select} {where} ORDER BY id DESC LIMIT @limit OFFSET @offset", p, ct).ConfigureAwait(false);
        return new PagedResult<AuditEventEntity>(items.ToList(), total, page, pageSize);
    }

    public static async Task<IReadOnlyList<AuditEventEntity>> ForEntityAsync(DbSession s, string entityType, string entityId, CancellationToken ct = default) =>
        (await s.QueryAsync<AuditEventEntity>($"{Select} WHERE entity_type = @entityType AND entity_id = @entityId ORDER BY id", new { entityType, entityId }, ct).ConfigureAwait(false)).ToList();

    /// <summary>Events after <paramref name="afterId"/> (for upload to the cloud).</summary>
    public static async Task<IReadOnlyList<AuditEventEntity>> AfterAsync(DbSession s, long afterId, int limit, CancellationToken ct = default) =>
        (await s.QueryAsync<AuditEventEntity>($"{Select} WHERE id > @afterId ORDER BY id LIMIT @limit", new { afterId, limit }, ct).ConfigureAwait(false)).ToList();
}
