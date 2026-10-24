using System.Text.RegularExpressions;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

public sealed record ServerAuditDto(long Id, DateTimeOffset AtUtc, string Username, string Action, string Detail, string? Ip);

/// <summary>Administration of sites, site keys and cloud users. Every change is recorded in the server audit log.</summary>
public sealed partial class CloudAdminService(Database db, CloudOptions options, CloudQueryService queries, TimeProvider time)
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,19}$")]
    private static partial Regex SiteCode();

    [GeneratedRegex("^[A-Za-z0-9._@-]{3,64}$")]
    private static partial Regex Username();

    // ------------------------------------------------------------------ sites

    public Task<List<AdminSiteDto>> SitesAsync(CancellationToken ct = default) => db.ReadAsync(async s =>
        (await CloudStore.SitesAsync(s, null, ct).ConfigureAwait(false)).Select(ToAdminDto).ToList(), ct);

    /// <summary>Registers a site and returns its key. The key is shown once; only its hash is stored.</summary>
    public async Task<CreateSiteResponse> CreateSiteAsync(CloudPrincipal admin, CreateSiteRequest request, string? ip, CancellationToken ct = default)
    {
        var code = request.Code?.Trim() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        if (!SiteCode().IsMatch(code)) throw new ArgumentException("The site code must be 1–20 letters, digits, '-' or '_' and must match the site code configured in the desktop application.");
        if (name.Length is 0 or > 200) throw new ArgumentException("Enter a site name (up to 200 characters).");
        var key = SiteKeys.Generate();
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            if (await CloudStore.SiteByCodeAsync(s, code, ct).ConfigureAwait(false) is not null) throw new InvalidOperationException($"A site with the code '{code}' already exists.");
            var site = new SiteRow { Code = code, Name = name, KeyHash = SiteKeys.Hash(key), KeyPrefix = SiteKeys.Prefix(key), IsActive = true, CreatedUtc = now };
            site.Id = await CloudStore.InsertSiteAsync(s, site, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, "SiteCreated", $"Site {code} ({name}) registered; key {site.KeyPrefix}…", ip, ct).ConfigureAwait(false);
            return new CreateSiteResponse(new SiteDto(site.Id, site.Code, site.Name, null, false, null), key);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Issues a new key. The old key stops working immediately (enter the new key in the desktop application).</summary>
    public async Task<CreateSiteResponse> RotateKeyAsync(CloudPrincipal admin, string code, string? ip, CancellationToken ct = default)
    {
        var key = SiteKeys.Generate();
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var site = await CloudStore.SiteByCodeAsync(s, code, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Site '{code}' not found.");
            await s.ExecuteAsync("UPDATE sites SET key_hash = @hash, key_prefix = @prefix WHERE id = @Id", new { hash = SiteKeys.Hash(key), prefix = SiteKeys.Prefix(key), site.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, "SiteKeyRotated", $"Site {site.Code}: key {site.KeyPrefix}… replaced by {SiteKeys.Prefix(key)}…", ip, ct).ConfigureAwait(false);
            return new CreateSiteResponse(new SiteDto(site.Id, site.Code, site.Name, site.LastHeartbeatUtc, queries.IsOnline(site), site.AppVersion), key);
        }, ct).ConfigureAwait(false);
    }

    public Task<AdminSiteDto> SetSiteActiveAsync(CloudPrincipal admin, string code, bool active, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.InTransactionAsync(async s =>
        {
            var site = await CloudStore.SiteByCodeAsync(s, code, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Site '{code}' not found.");
            await s.ExecuteAsync("UPDATE sites SET is_active = @active WHERE id = @Id", new { active, site.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, active ? "SiteActivated" : "SiteDeactivated", $"Site {site.Code}", ip, ct).ConfigureAwait(false);
            site.IsActive = active;
            return ToAdminDto(site);
        }, ct);
    }

    private AdminSiteDto ToAdminDto(SiteRow s) => new(s.Id, s.Code, s.Name, s.IsActive, s.KeyPrefix, s.CreatedUtc, s.LastHeartbeatUtc, queries.IsOnline(s), s.AppVersion);

    // ------------------------------------------------------------------ users

    public Task<List<CloudUserDto>> UsersAsync(CancellationToken ct = default) => db.ReadAsync(async s =>
    {
        var list = new List<CloudUserDto>();
        foreach (var u in await CloudStore.UsersAsync(s, ct).ConfigureAwait(false)) list.Add(await ToDtoAsync(s, u, ct).ConfigureAwait(false));
        return list;
    }, ct);

    public async Task<CloudUserDto> CreateUserAsync(CloudPrincipal admin, CreateUserRequest request, string? ip, CancellationToken ct = default)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        if (!Username().IsMatch(username)) throw new ArgumentException("The user name must be 3–64 characters: letters, digits, '.', '_', '-' or '@'.");
        var display = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim();
        if (!Roles.All.Contains(request.Role)) throw new ArgumentException($"Unknown role '{request.Role}'. Roles: {string.Join(", ", Roles.All)}.");
        var problems = options.PasswordPolicy.Check(request.Password ?? string.Empty, username);
        if (problems.Count > 0) throw new ArgumentException(string.Join(' ', problems));
        var now = time.GetUtcNow();
        var hash = PasswordHasher.Hash(request.Password!);
        return await db.InTransactionAsync(async s =>
        {
            if (await CloudStore.UserByNameAsync(s, username, ct).ConfigureAwait(false) is not null) throw new InvalidOperationException($"The user name '{username}' is already taken.");
            var siteIds = await ResolveSitesAsync(s, request.Sites, ct).ConfigureAwait(false);
            var u = new CloudUserRow { Username = username, DisplayName = CloudStore.Clip(display, 200), PasswordHash = hash, Role = request.Role, IsActive = true, MustChangePassword = true, CreatedUtc = now };
            u.Id = await CloudStore.InsertUserAsync(s, u, ct).ConfigureAwait(false);
            await CloudStore.SetUserSitesAsync(s, u.Id, siteIds, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, "UserCreated", $"{username} ({request.Role}); sites: {string.Join(", ", request.Sites ?? [])}", ip, ct).ConfigureAwait(false);
            return await ToDtoAsync(s, u, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public Task<CloudUserDto> UpdateUserAsync(CloudPrincipal admin, long id, UpdateUserRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.InTransactionAsync(async s =>
        {
            var u = await CloudStore.UserAsync(s, id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("User not found.");
            var changes = new List<string>();
            if (request.Role is { } role && role != u.Role)
            {
                if (!Roles.All.Contains(role)) throw new ArgumentException($"Unknown role '{role}'.");
                if (u.Role == Roles.Administrator) await EnsureAnotherAdminAsync(s, u.Id, ct).ConfigureAwait(false);
                changes.Add($"role {u.Role} → {role}");
                u.Role = role;
            }
            if (request.IsActive is { } active && active != u.IsActive)
            {
                if (!active && u.Id == admin.UserId) throw new InvalidOperationException("You cannot disable your own account.");
                if (!active && u.Role == Roles.Administrator) await EnsureAnotherAdminAsync(s, u.Id, ct).ConfigureAwait(false);
                changes.Add(active ? "enabled" : "disabled");
                u.IsActive = active;
            }
            if (!string.IsNullOrWhiteSpace(request.DisplayName) && request.DisplayName.Trim() != u.DisplayName)
            {
                u.DisplayName = CloudStore.Clip(request.DisplayName.Trim(), 200);
                changes.Add("display name");
            }
            await s.ExecuteAsync("UPDATE cloud_users SET role = @Role, is_active = @IsActive, display_name = @DisplayName WHERE id = @Id", u, ct).ConfigureAwait(false);
            if (request.Sites is not null)
            {
                await CloudStore.SetUserSitesAsync(s, u.Id, await ResolveSitesAsync(s, request.Sites, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                changes.Add($"sites: {string.Join(", ", request.Sites)}");
            }
            if (!u.IsActive) await s.ExecuteAsync("UPDATE refresh_tokens SET revoked_utc = @now WHERE user_id = @Id AND revoked_utc IS NULL", new { now, u.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, "UserUpdated", $"{u.Username}: {(changes.Count == 0 ? "no change" : string.Join("; ", changes))}", ip, ct).ConfigureAwait(false);
            return await ToDtoAsync(s, u, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Sets a temporary password (the user must change it at the next sign-in), unlocks the account and ends its sessions.</summary>
    public async Task ResetPasswordAsync(CloudPrincipal admin, long id, ResetPasswordRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await db.InTransactionAsync(async s =>
        {
            var u = await CloudStore.UserAsync(s, id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("User not found.");
            var problems = options.PasswordPolicy.Check(request.NewPassword ?? string.Empty, u.Username);
            if (problems.Count > 0) throw new ArgumentException(string.Join(' ', problems));
            await s.ExecuteAsync("UPDATE cloud_users SET password_hash = @hash, must_change_password = @must, failed_attempts = 0, locked_until_utc = NULL WHERE id = @Id",
                new { hash = PasswordHasher.Hash(request.NewPassword!), must = true, u.Id }, ct).ConfigureAwait(false);
            await s.ExecuteAsync("UPDATE refresh_tokens SET revoked_utc = @now WHERE user_id = @Id AND revoked_utc IS NULL", new { now, u.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, "PasswordReset", $"{u.Username}: temporary password set, sessions ended", ip, ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public Task UnlockAsync(CloudPrincipal admin, long id, string? ip, CancellationToken ct = default) => Simple(admin, id, ip, "UserUnlocked",
        "UPDATE cloud_users SET failed_attempts = 0, locked_until_utc = NULL WHERE id = @Id", ct);

    public Task ResetMfaAsync(CloudPrincipal admin, long id, string? ip, CancellationToken ct = default) => Simple(admin, id, ip, "MfaReset",
        "UPDATE cloud_users SET mfa_secret = NULL, mfa_last_step = NULL WHERE id = @Id", ct);

    private Task Simple(CloudPrincipal admin, long id, string? ip, string action, string sql, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.InTransactionAsync(async s =>
        {
            var u = await CloudStore.UserAsync(s, id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("User not found.");
            await s.ExecuteAsync(sql, new { u.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, admin.Username, action, u.Username, ip, ct).ConfigureAwait(false);
            return 0;
        }, ct);
    }

    private static async Task EnsureAnotherAdminAsync(DbSession s, long exceptId, CancellationToken ct)
    {
        var others = await s.QuerySingleAsync<long>("SELECT COUNT(*) FROM cloud_users WHERE role = @role AND is_active = @active AND id <> @exceptId",
            new { role = Roles.Administrator, active = true, exceptId }, ct).ConfigureAwait(false);
        if (others == 0) throw new InvalidOperationException("At least one active administrator must remain.");
    }

    private static async Task<List<long>> ResolveSitesAsync(DbSession s, IReadOnlyList<string>? codes, CancellationToken ct)
    {
        var ids = new List<long>();
        foreach (var code in codes ?? [])
        {
            var site = await CloudStore.SiteByCodeAsync(s, code.Trim(), ct).ConfigureAwait(false) ?? throw new ArgumentException($"Unknown site '{code}'.");
            ids.Add(site.Id);
        }
        return ids;
    }

    private async Task<CloudUserDto> ToDtoAsync(DbSession s, CloudUserRow u, CancellationToken ct) => new(u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.MfaSecret is not null,
        u.LockedUntilUtc > time.GetUtcNow(), u.MustChangePassword, u.LastLoginUtc, (await CloudStore.UserSitesAsync(s, u.Id, ct).ConfigureAwait(false)).Select(x => x.Code).ToList());

    /// <summary>Creates the first administrator on an empty server (from configuration). Returns false when users already exist.</summary>
    public async Task<bool> BootstrapAsync(ILogger log, CancellationToken ct = default)
    {
        var count = await db.ReadAsync(s => CloudStore.CountUsersAsync(s, ct), ct).ConfigureAwait(false);
        if (count > 0) return false;
        var username = options.BootstrapAdminUsername?.Trim();
        var password = options.BootstrapAdminPassword;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("The server has no users yet. Set Cloud__BootstrapAdminUsername and Cloud__BootstrapAdminPassword for the first start.");
        }
        if (!Username().IsMatch(username)) throw new InvalidOperationException("Cloud:BootstrapAdminUsername is not a valid user name.");
        var problems = options.PasswordPolicy.Check(password, username);
        if (problems.Count > 0) throw new InvalidOperationException("Cloud:BootstrapAdminPassword is too weak: " + string.Join(' ', problems));
        var now = time.GetUtcNow();
        await db.InTransactionAsync(async s =>
        {
            await CloudStore.InsertUserAsync(s, new CloudUserRow
            {
                Username = username, DisplayName = "Administrator", PasswordHash = PasswordHasher.Hash(password), Role = Roles.Administrator, IsActive = true, MustChangePassword = true, CreatedUtc = now,
            }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, "system", "BootstrapAdmin", $"First administrator '{username}' created from configuration", null, ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
        log.LogWarning("Created the first administrator '{Username}'. Sign in and change the password; then remove the bootstrap password from the configuration.", username);
        return true;
    }

    // ------------------------------------------------------------------ review lists

    public Task<List<ConflictDto>> ConflictsAsync(CancellationToken ct = default) => db.ReadAsync(async s =>
        (await s.QueryAsync<ConflictRow>("""
            SELECT c.id, s.code AS site_code, c.entity_type, c.uid, c.local_version, c.server_version, c.at_utc
            FROM sync_conflicts c JOIN sites s ON s.id = c.site_id ORDER BY c.id DESC LIMIT 500
            """, ct: ct).ConfigureAwait(false))
        .Select(c => new ConflictDto(c.Id, c.SiteCode, c.EntityType, c.Uid, (int?)c.LocalVersion, (int?)c.ServerVersion, c.AtUtc)).ToList(), ct);

    public Task<List<SiteUserListDto>> SiteUsersAsync(CancellationToken ct = default) => db.ReadAsync(async s =>
        (await s.QueryAsync<SiteUserRow>("""
            SELECT s.code AS site_code, u.username, u.display_name, u.role, u.is_active, u.last_login_utc FROM site_users u JOIN sites s ON s.id = u.site_id ORDER BY s.code, u.username
            """, ct: ct).ConfigureAwait(false))
        .Select(u => new SiteUserListDto(u.SiteCode, u.Username, u.DisplayName, u.Role, u.IsActive, u.LastLoginUtc)).ToList(), ct);

    public Task<List<ServerAuditDto>> ServerAuditAsync(int limit, CancellationToken ct = default) => db.ReadAsync(async s =>
        (await s.QueryAsync<ServerAuditRow>("SELECT id, at_utc, username, action, detail, ip FROM server_audit ORDER BY id DESC LIMIT @limit",
            new { limit = Math.Clamp(limit, 1, 2000) }, ct).ConfigureAwait(false)).Select(a => new ServerAuditDto(a.Id, a.AtUtc, a.Username, a.Action, a.Detail, a.Ip)).ToList(), ct);
}
