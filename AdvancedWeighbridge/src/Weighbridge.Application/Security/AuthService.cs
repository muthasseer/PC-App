using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Security;

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    LockedOut,
    Disabled,
    MfaRequired,
    InvalidMfaCode,
}

public sealed record LoginResult(LoginOutcome Outcome, UserContext? User, bool MustChangePassword, DateTimeOffset? LockedUntil, string Message);

public sealed record SupervisorApproval(long UserId, string Username, string DisplayName, DateTimeOffset At);

/// <summary>
/// Local authentication: PBKDF2 passwords, lock-out after repeated failures, optional TOTP, login history,
/// supervisor approvals (credentials typed at the operator's station) and user administration.
/// </summary>
public sealed class AuthService(Database db, SettingsService settings, AuditService audit, ISecretProtector protector, TimeProvider time)
{
    // Precomputed hash so that unknown user names take as long as wrong passwords (no user enumeration by timing).
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("not-a-real-password", PasswordHasher.DefaultIterations));

    public async Task<LoginResult> LoginAsync(string username, string password, string? totpCode = null, CancellationToken ct = default)
    {
        var security = await settings.GetAsync<SecuritySettings>(SettingKeys.Security, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var machine = Environment.MachineName;
        return await db.InTransactionAsync(async s =>
        {
            var user = await UserStore.FindByUsernameAsync(s, username.Trim(), ct).ConfigureAwait(false);
            if (user is null)
            {
                PasswordHasher.Verify(password, DummyHash.Value);
                await History(s, null, username, false, "unknown user", machine, now, ct).ConfigureAwait(false);
                return new LoginResult(LoginOutcome.InvalidCredentials, null, false, null, "Invalid user name or password.");
            }
            if (!user.IsActive)
            {
                await History(s, user.Id, user.Username, false, "disabled", machine, now, ct).ConfigureAwait(false);
                return new LoginResult(LoginOutcome.Disabled, null, false, null, "This account is disabled.");
            }
            if (user.LockedUntilUtc is { } until && until > now)
            {
                await History(s, user.Id, user.Username, false, "locked", machine, now, ct).ConfigureAwait(false);
                return new LoginResult(LoginOutcome.LockedOut, null, false, until, $"Too many failed attempts. Try again after {until.ToLocalTime():HH:mm}.");
            }
            if (!PasswordHasher.Verify(password, user.PasswordHash))
            {
                var attempts = user.FailedAttempts + 1;
                DateTimeOffset? lockUntil = attempts >= security.MaxFailedLogins ? now.AddMinutes(security.LockoutMinutes) : null;
                await UserStore.RecordLoginFailureAsync(s, user.Id, lockUntil is null ? attempts : 0, lockUntil, ct).ConfigureAwait(false);
                await History(s, user.Id, user.Username, false, "bad password", machine, now, ct).ConfigureAwait(false);
                if (lockUntil is not null)
                {
                    await audit.WriteAsync(s, UserContext.System, "Security", "LockedOut", $"User {user.Username} locked out after {attempts} failed logins", "User", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
                    return new LoginResult(LoginOutcome.LockedOut, null, false, lockUntil, $"Too many failed attempts. The account is locked for {security.LockoutMinutes} minutes.");
                }
                return new LoginResult(LoginOutcome.InvalidCredentials, null, false, null, "Invalid user name or password.");
            }
            if (user.MfaEnabled)
            {
                if (string.IsNullOrWhiteSpace(totpCode)) return new LoginResult(LoginOutcome.MfaRequired, null, false, null, "Enter the code from your authenticator app.");
                var secret = protector.Unprotect(user.MfaSecretProtected!);
                if (!Totp.Verify(secret, totpCode, now, user.MfaLastStep, out var step))
                {
                    await History(s, user.Id, user.Username, false, "bad MFA code", machine, now, ct).ConfigureAwait(false);
                    return new LoginResult(LoginOutcome.InvalidMfaCode, null, false, null, "The authenticator code is not valid.");
                }
                await UserStore.SetMfaLastStepAsync(s, user.Id, step, ct).ConfigureAwait(false);
            }

            await UserStore.RecordLoginSuccessAsync(s, user.Id, now, ct).ConfigureAwait(false);
            if (PasswordHasher.NeedsRehash(user.PasswordHash))
            {
                await UserStore.SetPasswordAsync(s, user.Id, PasswordHasher.Hash(password), user.MustChangePassword, now, ct).ConfigureAwait(false);
            }
            await History(s, user.Id, user.Username, true, null, machine, now, ct).ConfigureAwait(false);
            var context = new UserContext(user.Id, user.Username, user.DisplayName, user.RoleName, await UserStore.PermissionsAsync(s, user.RoleId, ct).ConfigureAwait(false));
            await audit.WriteAsync(s, context, "Security", "Login", $"{user.Username} signed in on {machine}", "User", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
            return new LoginResult(LoginOutcome.Success, context, user.MustChangePassword, null, "Signed in.");
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A supervisor authorises an action at the operator's station by entering their own credentials.
    /// Returns null (and audits the attempt) when the credentials or permission are not sufficient.
    /// </summary>
    public async Task<SupervisorApproval?> VerifySupervisorAsync(string username, string password, string permission, string action, CancellationToken ct = default)
    {
        var result = await LoginAsync(username, password, null, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (result.Outcome != LoginOutcome.Success || result.User is null || !result.User.Has(permission))
        {
            await audit.WriteAsync(UserContext.System, "Security", "ApprovalDenied", $"Supervisor approval for '{action}' refused for '{username}'", reason: result.Outcome.ToString(), ct: ct).ConfigureAwait(false);
            return null;
        }
        return new SupervisorApproval(result.User.UserId, result.User.Username, result.User.DisplayName, now);
    }

    public async Task ChangePasswordAsync(UserContext user, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var policy = await PolicyAsync(ct).ConfigureAwait(false);
        var problems = policy.Check(newPassword, user.Username);
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        await db.InTransactionAsync(async s =>
        {
            var u = await UserStore.GetAsync(s, user.UserId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("User not found.");
            if (!PasswordHasher.Verify(currentPassword, u.PasswordHash)) throw new UnauthorizedAccessException("The current password is not correct.");
            if (PasswordHasher.Verify(newPassword, u.PasswordHash)) throw new ArgumentException("The new password must be different.");
            await UserStore.SetPasswordAsync(s, u.Id, PasswordHasher.Hash(newPassword), false, time.GetUtcNow(), ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Security", "PasswordChanged", $"{u.Username} changed their password", "User", u.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<long> CreateUserAsync(UserContext admin, string username, string displayName, string password, string role, bool mustChangePassword = true, CancellationToken ct = default)
    {
        admin.Demand(Permissions.UsersManage);
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 || username.Any(char.IsWhiteSpace)) throw new ArgumentException("The user name must not be empty or contain spaces.");
        var problems = (await PolicyAsync(ct).ConfigureAwait(false)).Check(password, username);
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var roleId = (await UserStore.RolesAsync(s, ct).ConfigureAwait(false)).FirstOrDefault(r => r.Name.Equals(role, StringComparison.OrdinalIgnoreCase))?.Id
                         ?? throw new ArgumentException($"Unknown role '{role}'.");
            if (await UserStore.FindByUsernameAsync(s, username, ct).ConfigureAwait(false) is not null) throw new ArgumentException($"The user name '{username}' is already used.");
            var id = await UserStore.InsertAsync(s, new UserEntity
            {
                Username = username.Trim(), DisplayName = displayName.Trim(), PasswordHash = PasswordHasher.Hash(password), RoleId = roleId,
                MustChangePassword = mustChangePassword, PasswordChangedUtc = now, CreatedUtc = now, UpdatedUtc = now,
            }, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, admin, "Users", "Created", $"User {username} ({role}) created", "User", id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                newJson: AuditService.ToJson(new { username, displayName, role }), ct: ct).ConfigureAwait(false);
            return id;
        }, ct).ConfigureAwait(false);
    }

    public async Task UpdateUserAsync(UserContext admin, long userId, string displayName, string role, bool isActive, CancellationToken ct = default)
    {
        admin.Demand(Permissions.UsersManage);
        if (userId == admin.UserId && !isActive) throw new InvalidOperationException("You cannot disable your own account.");
        await db.InTransactionAsync(async s =>
        {
            var u = await UserStore.GetAsync(s, userId, ct).ConfigureAwait(false) ?? throw new ArgumentException("User not found.");
            var roles = await UserStore.RolesAsync(s, ct).ConfigureAwait(false);
            var roleEntity = roles.FirstOrDefault(r => r.Name.Equals(role, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"Unknown role '{role}'.");
            var before = new { u.DisplayName, Role = u.RoleName, u.IsActive };
            u.DisplayName = displayName;
            u.RoleId = roleEntity.Id;
            u.IsActive = isActive;
            u.UpdatedUtc = time.GetUtcNow();
            await UserStore.UpdateProfileAsync(s, u, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, admin, "Users", "Updated", $"User {u.Username} updated", "User", userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AuditService.ToJson(before), AuditService.ToJson(new { DisplayName = displayName, Role = roleEntity.Name, IsActive = isActive }), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task ResetPasswordAsync(UserContext admin, long userId, string temporaryPassword, CancellationToken ct = default)
    {
        admin.Demand(Permissions.UsersManage);
        await db.InTransactionAsync(async s =>
        {
            var u = await UserStore.GetAsync(s, userId, ct).ConfigureAwait(false) ?? throw new ArgumentException("User not found.");
            var problems = (await PolicyAsync(ct).ConfigureAwait(false)).Check(temporaryPassword, u.Username);
            if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
            await UserStore.SetPasswordAsync(s, userId, PasswordHasher.Hash(temporaryPassword), mustChange: true, time.GetUtcNow(), ct).ConfigureAwait(false);
            await audit.WriteAsync(s, admin, "Users", "PasswordReset", $"Password of {u.Username} reset (must change at next login)", "User", userId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Starts MFA enrolment: returns the secret and the otpauth:// URI for the authenticator app.</summary>
    public static (string Secret, string Uri) BeginMfaEnrolment(UserContext user, string issuer)
    {
        var secret = Totp.NewSecret();
        return (secret, Totp.ProvisioningUri(issuer, user.Username, secret));
    }

    public async Task<bool> CompleteMfaEnrolmentAsync(UserContext user, string secret, string code, CancellationToken ct = default)
    {
        if (!Totp.Verify(secret, code, time.GetUtcNow(), null, out _)) return false;
        await db.InTransactionAsync(async s =>
        {
            await UserStore.SetMfaAsync(s, user.UserId, protector.Protect(secret), time.GetUtcNow(), ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Security", "MfaEnabled", $"{user.Username} enabled two-factor authentication", "User", user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
        return true;
    }

    public async Task DisableMfaAsync(UserContext admin, long userId, CancellationToken ct = default)
    {
        if (admin.UserId != userId) admin.Demand(Permissions.UsersManage);
        await db.InTransactionAsync(async s =>
        {
            await UserStore.SetMfaAsync(s, userId, null, time.GetUtcNow(), ct).ConfigureAwait(false);
            await audit.WriteAsync(s, admin, "Security", "MfaDisabled", $"Two-factor authentication disabled for user {userId}", "User", userId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserEntity>> UsersAsync(UserContext admin, CancellationToken ct = default)
    {
        admin.Demand(Permissions.UsersManage);
        return await db.ReadAsync(s => UserStore.ListAsync(s, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RoleEntity>> RolesAsync(CancellationToken ct = default) => await db.ReadAsync(s => UserStore.RolesAsync(s, ct), ct).ConfigureAwait(false);

    public async Task SetRolePermissionsAsync(UserContext admin, long roleId, IReadOnlyCollection<string> permissions, CancellationToken ct = default)
    {
        admin.Demand(Permissions.RolesManage);
        var unknown = permissions.Except(Permissions.All).ToList();
        if (unknown.Count > 0) throw new ArgumentException("Unknown permissions: " + string.Join(", ", unknown));
        await db.InTransactionAsync(async s =>
        {
            var role = (await UserStore.RolesAsync(s, ct).ConfigureAwait(false)).Single(r => r.Id == roleId);
            if (role.Name == Roles.Administrator && !permissions.Contains(Permissions.UsersManage)) throw new InvalidOperationException("Administrators must keep the user management permission.");
            await UserStore.SetRolePermissionsAsync(s, roleId, permissions, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, admin, "Users", "RolePermissions", $"Permissions of role {role.Name} changed", "Role", roleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AuditService.ToJson(role.Permissions), AuditService.ToJson(permissions.OrderBy(p => p)), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<PasswordPolicy> PolicyAsync(CancellationToken ct = default)
    {
        var sec = await settings.GetAsync<SecuritySettings>(SettingKeys.Security, ct).ConfigureAwait(false);
        return new PasswordPolicy { MinimumLength = sec.PasswordMinLength };
    }

    private static Task History(DbSession s, long? userId, string username, bool success, string? reason, string machine, DateTimeOffset at, CancellationToken ct) =>
        UserStore.AddLoginHistoryAsync(s, new LoginHistoryEntity { UserId = userId, Username = username, Success = success, Reason = reason, Machine = machine, AtUtc = at }, ct);
}

/// <summary>The signed-in user of the desktop session with idle timeout.</summary>
public sealed class SessionContext(TimeProvider time)
{
    private DateTimeOffset _lastActivity = time.GetUtcNow();

    public UserContext? User { get; private set; }
    public bool IsSignedIn => User is not null;
    public int TimeoutMinutes { get; set; } = 30;

    public event EventHandler? SignedOut;

    public void SignIn(UserContext user)
    {
        User = user;
        Touch();
    }

    public void SignOut()
    {
        if (User is null) return;
        User = null;
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    public void Touch() => _lastActivity = time.GetUtcNow();

    /// <summary>Signs out when idle for longer than the timeout. Returns true when the session expired.</summary>
    public bool CheckTimeout()
    {
        if (User is null || TimeoutMinutes <= 0) return false;
        if ((time.GetUtcNow() - _lastActivity).TotalMinutes < TimeoutMinutes) return false;
        SignOut();
        return true;
    }

    public UserContext Require() => User ?? throw new UnauthorizedAccessException("Please sign in.");
}
