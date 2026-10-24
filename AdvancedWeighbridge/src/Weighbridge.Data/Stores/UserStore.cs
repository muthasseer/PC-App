namespace Weighbridge.Data.Stores;

public static class UserStore
{
    private const string UserColumns = """
        u.id, u.uid, u.username, u.display_name, u.password_hash, u.role_id, r.name AS role_name, u.is_active,
        u.must_change_password, u.mfa_secret_protected, u.mfa_last_step, u.failed_attempts, u.locked_until_utc,
        u.last_login_utc, u.password_changed_utc, u.created_utc, u.updated_utc
        """;

    public static async Task<long> EnsureRoleAsync(DbSession s, string name, string? description, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        var id = await s.ExecuteScalarAsync<long?>("SELECT id FROM roles WHERE lower(name) = lower(@name)", new { name }, ct).ConfigureAwait(false);
        if (id is null)
        {
            id = await s.ExecuteScalarAsync<long>("INSERT INTO roles (name, description, is_system) VALUES (@name, @description, @system) RETURNING id",
                new { name, description, system = true }, ct).ConfigureAwait(false);
            foreach (var p in permissions)
            {
                await s.ExecuteAsync("INSERT INTO role_permissions (role_id, permission) VALUES (@id, @p)", new { id, p }, ct).ConfigureAwait(false);
            }
        }
        return id.Value;
    }

    public static async Task<IReadOnlyList<RoleEntity>> RolesAsync(DbSession s, CancellationToken ct = default)
    {
        var roles = (await s.QueryAsync<RoleEntity>("SELECT id, name, description, is_system FROM roles ORDER BY id", ct: ct).ConfigureAwait(false)).ToList();
        var perms = await s.QueryAsync<(long RoleId, string Permission)>("SELECT role_id, permission FROM role_permissions", ct: ct).ConfigureAwait(false);
        var byRole = perms.ToLookup(p => p.RoleId, p => p.Permission);
        foreach (var r in roles) r.Permissions = byRole[r.Id].OrderBy(p => p, StringComparer.Ordinal).ToList();
        return roles;
    }

    public static async Task SetRolePermissionsAsync(DbSession s, long roleId, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        await s.ExecuteAsync("DELETE FROM role_permissions WHERE role_id = @roleId", new { roleId }, ct).ConfigureAwait(false);
        foreach (var p in permissions.Distinct(StringComparer.Ordinal))
        {
            await s.ExecuteAsync("INSERT INTO role_permissions (role_id, permission) VALUES (@roleId, @p)", new { roleId, p }, ct).ConfigureAwait(false);
        }
    }

    public static async Task<IReadOnlySet<string>> PermissionsAsync(DbSession s, long roleId, CancellationToken ct = default) =>
        (await s.QueryAsync<string>("SELECT permission FROM role_permissions WHERE role_id = @roleId", new { roleId }, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    public static Task<UserEntity?> FindByUsernameAsync(DbSession s, string username, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<UserEntity>($"SELECT {UserColumns} FROM users u JOIN roles r ON r.id = u.role_id WHERE lower(u.username) = lower(@username)", new { username }, ct);

    public static Task<UserEntity?> GetAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<UserEntity>($"SELECT {UserColumns} FROM users u JOIN roles r ON r.id = u.role_id WHERE u.id = @id", new { id }, ct);

    public static async Task<IReadOnlyList<UserEntity>> ListAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<UserEntity>($"SELECT {UserColumns} FROM users u JOIN roles r ON r.id = u.role_id ORDER BY u.username", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<long> CountAsync(DbSession s, CancellationToken ct = default) => s.QuerySingleAsync<long>("SELECT COUNT(*) FROM users", ct: ct);

    public static Task<long> InsertAsync(DbSession s, UserEntity u, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO users (uid, username, display_name, password_hash, role_id, is_active, must_change_password, mfa_secret_protected,
                           failed_attempts, password_changed_utc, created_utc, updated_utc)
        VALUES (@Uid, @Username, @DisplayName, @PasswordHash, @RoleId, @IsActive, @MustChangePassword, @MfaSecretProtected,
                0, @PasswordChangedUtc, @CreatedUtc, @UpdatedUtc)
        RETURNING id
        """, u, ct)!;

    public static Task<int> UpdateProfileAsync(DbSession s, UserEntity u, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE users SET display_name = @DisplayName, role_id = @RoleId, is_active = @IsActive, must_change_password = @MustChangePassword,
                         updated_utc = @UpdatedUtc
        WHERE id = @Id
        """, u, ct);

    public static Task<int> SetPasswordAsync(DbSession s, long id, string hash, bool mustChange, DateTimeOffset at, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE users SET password_hash = @hash, must_change_password = @mustChange, password_changed_utc = @at, updated_utc = @at,
                         failed_attempts = 0, locked_until_utc = NULL
        WHERE id = @id
        """, new { id, hash, mustChange, at }, ct);

    public static Task<int> SetMfaAsync(DbSession s, long id, string? protectedSecret, DateTimeOffset at, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE users SET mfa_secret_protected = @protectedSecret, mfa_last_step = NULL, updated_utc = @at WHERE id = @id", new { id, protectedSecret, at }, ct);

    public static Task<int> SetMfaLastStepAsync(DbSession s, long id, long step, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE users SET mfa_last_step = @step WHERE id = @id", new { id, step }, ct);

    public static Task<int> RecordLoginSuccessAsync(DbSession s, long id, DateTimeOffset at, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE users SET failed_attempts = 0, locked_until_utc = NULL, last_login_utc = @at WHERE id = @id", new { id, at }, ct);

    public static Task<int> RecordLoginFailureAsync(DbSession s, long id, int failedAttempts, DateTimeOffset? lockedUntil, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE users SET failed_attempts = @failedAttempts, locked_until_utc = @lockedUntil WHERE id = @id", new { id, failedAttempts, lockedUntil }, ct);

    public static Task<int> AddLoginHistoryAsync(DbSession s, LoginHistoryEntity h, CancellationToken ct = default) => s.ExecuteAsync("""
        INSERT INTO login_history (user_id, username, success, reason, machine, at_utc) VALUES (@UserId, @Username, @Success, @Reason, @Machine, @AtUtc)
        """, h, ct);

    public static async Task<IReadOnlyList<LoginHistoryEntity>> LoginHistoryAsync(DbSession s, int limit = 200, CancellationToken ct = default) =>
        (await s.QueryAsync<LoginHistoryEntity>("SELECT id, user_id, username, success, reason, machine, at_utc FROM login_history ORDER BY id DESC LIMIT @limit", new { limit }, ct).ConfigureAwait(false)).ToList();
}
