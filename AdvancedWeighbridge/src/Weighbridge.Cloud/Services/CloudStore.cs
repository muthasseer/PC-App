using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

/// <summary>Site keys: 256-bit random secrets. Only the SHA-256 hash and a short display prefix are stored.</summary>
public static class SiteKeys
{
    public static string Generate() => "awms_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))).ToLowerInvariant();

    public static string Prefix(string key) => key.Length > 10 ? key[..10] : key;

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Shared SQL of the cloud schema. Works on SQLite and PostgreSQL.</summary>
public static class CloudStore
{
    private const string SiteColumns = "id, code, name, key_hash, key_prefix, is_active, created_utc, last_heartbeat_utc, app_version, heartbeat_json, offline_notified";
    private const string UserColumns = "id, username, display_name, password_hash, role, is_active, must_change_password, mfa_secret, mfa_last_step, failed_attempts, locked_until_utc, last_login_utc, created_utc";

    // ------------------------------------------------------------------ sites

    public static Task<SiteRow?> SiteByKeyHashAsync(DbSession s, string hash, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<SiteRow>($"SELECT {SiteColumns} FROM sites WHERE key_hash = @hash", new { hash }, ct);

    public static Task<SiteRow?> SiteByCodeAsync(DbSession s, string code, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<SiteRow>($"SELECT {SiteColumns} FROM sites WHERE lower(code) = lower(@code)", new { code }, ct);

    public static Task<SiteRow?> SiteAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<SiteRow>($"SELECT {SiteColumns} FROM sites WHERE id = @id", new { id }, ct);

    public static async Task<IReadOnlyList<SiteRow>> SitesAsync(DbSession s, IReadOnlyCollection<long>? only, CancellationToken ct = default) =>
        (await s.QueryAsync<SiteRow>($"SELECT {SiteColumns} FROM sites{(only is null ? string.Empty : " WHERE id IN @only")} ORDER BY code", new { only }, ct).ConfigureAwait(false)).ToList();

    public static Task<long> InsertSiteAsync(DbSession s, SiteRow site, CancellationToken ct = default) =>
        s.QuerySingleAsync<long>("""
            INSERT INTO sites (code, name, key_hash, key_prefix, is_active, created_utc, offline_notified)
            VALUES (@Code, @Name, @KeyHash, @KeyPrefix, @IsActive, @CreatedUtc, @OfflineNotified) RETURNING id
            """, site, ct);

    // ------------------------------------------------------------------ users

    public static Task<CloudUserRow?> UserByNameAsync(DbSession s, string username, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<CloudUserRow>($"SELECT {UserColumns} FROM cloud_users WHERE lower(username) = lower(@username)", new { username }, ct);

    public static Task<CloudUserRow?> UserAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<CloudUserRow>($"SELECT {UserColumns} FROM cloud_users WHERE id = @id", new { id }, ct);

    public static async Task<IReadOnlyList<CloudUserRow>> UsersAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<CloudUserRow>($"SELECT {UserColumns} FROM cloud_users ORDER BY username", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<long> CountUsersAsync(DbSession s, CancellationToken ct = default) => s.QuerySingleAsync<long>("SELECT COUNT(*) FROM cloud_users", ct: ct);

    public static Task<long> InsertUserAsync(DbSession s, CloudUserRow u, CancellationToken ct = default) =>
        s.QuerySingleAsync<long>("""
            INSERT INTO cloud_users (username, display_name, password_hash, role, is_active, must_change_password, failed_attempts, created_utc)
            VALUES (@Username, @DisplayName, @PasswordHash, @Role, @IsActive, @MustChangePassword, 0, @CreatedUtc) RETURNING id
            """, u, ct);

    public static async Task<IReadOnlyList<(long SiteId, string Code)>> UserSitesAsync(DbSession s, long userId, CancellationToken ct = default) =>
        (await s.QueryAsync<(long, string)>("SELECT s.id, s.code FROM cloud_user_sites us JOIN sites s ON s.id = us.site_id WHERE us.user_id = @userId ORDER BY s.code",
            new { userId }, ct).ConfigureAwait(false)).ToList();

    public static async Task SetUserSitesAsync(DbSession s, long userId, IEnumerable<long> siteIds, CancellationToken ct = default)
    {
        await s.ExecuteAsync("DELETE FROM cloud_user_sites WHERE user_id = @userId", new { userId }, ct).ConfigureAwait(false);
        foreach (var siteId in siteIds.Distinct())
        {
            await s.ExecuteAsync("INSERT INTO cloud_user_sites (user_id, site_id) VALUES (@userId, @siteId)", new { userId, siteId }, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ notifications and audit

    public static Task<int> AddNotificationAsync(DbSession s, long siteId, Guid uid, DateTimeOffset at, string severity, string category, string title, string message,
        string? entityType, string? entityId, DateTimeOffset received, CancellationToken ct = default) =>
        s.ExecuteAsync("""
            INSERT INTO cloud_notifications (site_id, uid, at_utc, severity, category, title, message, entity_type, entity_id, received_utc)
            VALUES (@siteId, @uid, @at, @severity, @category, @title, @message, @entityType, @entityId, @received)
            """, new { siteId, uid, at, severity, category, title = Clip(title, 200), message = Clip(message, 2000), entityType, entityId, received }, ct);

    public static Task<int> ServerAuditAsync(DbSession s, DateTimeOffset at, string username, string action, string detail, string? ip, CancellationToken ct = default) =>
        s.ExecuteAsync("INSERT INTO server_audit (at_utc, username, action, detail, ip) VALUES (@at, @username, @action, @detail, @ip)",
            new { at, username, action, detail = Clip(detail, 2000), ip }, ct);

    public static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>True for a unique-constraint violation on either engine (a concurrent insert of the same row).</summary>
    public static bool IsUniqueViolation(Exception e) => e switch
    {
        Microsoft.Data.Sqlite.SqliteException sq => sq.SqliteErrorCode == 19,
        Npgsql.PostgresException pg => pg.SqlState == "23505",
        DbException { InnerException: { } inner } => IsUniqueViolation(inner),
        _ => false,
    };
}
