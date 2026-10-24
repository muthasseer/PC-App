using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using QRCoder;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

public sealed record AuthOutcome(LoginResponse? Response, string? Error, bool MfaRequired = false);

/// <summary>
/// Cloud user sign-in: PBKDF2 passwords, lock-out after repeated failures, optional TOTP (replay-protected), short-lived
/// access tokens and rotating refresh tokens (re-use of a rotated token revokes the whole session family).
/// </summary>
public sealed class CloudAuthService(Database db, CloudOptions options, CloudKeys keys, SecretBox box, TimeProvider time, ILogger<CloudAuthService> log)
{
    public const string Audience = "awms-api";
    public const string RestrictionPassword = "password";
    public const string RestrictionMfa = "mfa";
    private const string GenericFailure = "Invalid user name or password.";
    private static readonly string DummyHash = PasswordHasher.Hash("dummy-password-for-timing", 600_000);

    public SymmetricSecurityKey SigningKey { get; } = new(keys.JwtKey);

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidIssuer = options.JwtIssuer,
        ValidAudience = Audience,
        IssuerSigningKey = SigningKey,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        // Lifetime is checked against the server's clock abstraction (the same one that issued the token).
        LifetimeValidator = (notBefore, expires, _, p) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return (notBefore is null || notBefore.Value <= now + p.ClockSkew) && expires is { } exp && exp > now - p.ClockSkew;
        },
        NameClaimType = CloudClaims.Name,
        RoleClaimType = CloudClaims.Role,
    };

    public async Task<AuthOutcome> LoginAsync(LoginRequest request, string? ip, CancellationToken ct = default)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        if (username.Length == 0 || string.IsNullOrEmpty(request.Password)) return new AuthOutcome(null, "Enter the user name and password.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var u = await LockUserAsync(s, username, ct).ConfigureAwait(false);
            if (u is null)
            {
                PasswordHasher.Verify(request.Password, DummyHash); // same work as for a real account
                await CloudStore.ServerAuditAsync(s, now, username, "LoginFailed", "Unknown user name", ip, ct).ConfigureAwait(false);
                return new AuthOutcome(null, GenericFailure);
            }
            if (u.LockedUntilUtc is { } until && until > now)
            {
                await CloudStore.ServerAuditAsync(s, now, u.Username, "LoginRefused", "Account locked", ip, ct).ConfigureAwait(false);
                return new AuthOutcome(null, $"The account is locked after repeated failed sign-ins. Try again after {until:HH:mm} UTC or ask an administrator to unlock it.");
            }
            if (!PasswordHasher.Verify(request.Password, u.PasswordHash))
            {
                await RegisterFailureAsync(s, u, now, ip, "Wrong password", ct).ConfigureAwait(false);
                return new AuthOutcome(null, GenericFailure);
            }
            if (!u.IsActive)
            {
                await CloudStore.ServerAuditAsync(s, now, u.Username, "LoginRefused", "Account disabled", ip, ct).ConfigureAwait(false);
                return new AuthOutcome(null, "This account is disabled.");
            }
            if (u.MfaSecret is not null)
            {
                if (string.IsNullOrWhiteSpace(request.TotpCode)) return new AuthOutcome(null, "Enter the 6-digit code from your authenticator app.", MfaRequired: true);
                if (!Totp.Verify(box.Unprotect(u.MfaSecret), request.TotpCode.Trim(), now, u.MfaLastStep, out var step))
                {
                    await RegisterFailureAsync(s, u, now, ip, "Wrong or re-used MFA code", ct).ConfigureAwait(false);
                    return new AuthOutcome(null, "The authentication code is not valid.", MfaRequired: true);
                }
                await s.ExecuteAsync("UPDATE cloud_users SET mfa_last_step = @step WHERE id = @Id", new { step, u.Id }, ct).ConfigureAwait(false);
            }
            var newHash = PasswordHasher.NeedsRehash(u.PasswordHash) ? PasswordHasher.Hash(request.Password) : u.PasswordHash;
            await s.ExecuteAsync("UPDATE cloud_users SET failed_attempts = 0, locked_until_utc = NULL, last_login_utc = @now, password_hash = @newHash WHERE id = @Id",
                new { now, newHash, u.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, u.Username, "LoginSucceeded", u.MfaSecret is null ? "Password" : "Password + MFA", ip, ct).ConfigureAwait(false);
            return new AuthOutcome(await IssueAsync(s, u, Guid.NewGuid().ToString("N"), now, ct).ConfigureAwait(false), null);
        }, ct).ConfigureAwait(false);
    }

    public async Task<AuthOutcome> RefreshAsync(string refreshToken, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return new AuthOutcome(null, "Session expired. Sign in again.");
        var hash = SiteKeys.Hash(refreshToken);
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var row = await s.QuerySingleOrDefaultAsync<RefreshTokenRow>(
                $"SELECT id, user_id, token_hash, family, expires_utc, created_utc, revoked_utc, replaced FROM refresh_tokens WHERE token_hash = @hash{s.Dialect.ForUpdate}",
                new { hash }, ct).ConfigureAwait(false);
            if (row is null) return new AuthOutcome(null, "Session expired. Sign in again.");
            if (row.RevokedUtc is { } revoked)
            {
                // A rotated token presented again (outside a short grace period for parallel requests) means it was copied: end the whole session.
                if (row.Replaced && now - revoked > TimeSpan.FromSeconds(30))
                {
                    await s.ExecuteAsync("UPDATE refresh_tokens SET revoked_utc = @now WHERE family = @Family AND revoked_utc IS NULL", new { now, row.Family }, ct).ConfigureAwait(false);
                    var who = await CloudStore.UserAsync(s, row.UserId, ct).ConfigureAwait(false);
                    await CloudStore.ServerAuditAsync(s, now, who?.Username ?? "?", "RefreshTokenReuse", "A rotated refresh token was used again; the session was revoked.", ip, ct).ConfigureAwait(false);
                    log.LogWarning("Refresh token re-use detected for user {UserId}; session family revoked", row.UserId);
                }
                return new AuthOutcome(null, "Session expired. Sign in again.");
            }
            if (row.ExpiresUtc <= now) return new AuthOutcome(null, "Session expired. Sign in again.");
            var u = await CloudStore.UserAsync(s, row.UserId, ct).ConfigureAwait(false);
            if (u is null || !u.IsActive || u.LockedUntilUtc > now) return new AuthOutcome(null, "This account is disabled or locked.");
            await s.ExecuteAsync("UPDATE refresh_tokens SET revoked_utc = @now, replaced = @replaced WHERE id = @Id", new { now, replaced = true, row.Id }, ct).ConfigureAwait(false);
            return new AuthOutcome(await IssueAsync(s, u, row.Family, now, ct).ConfigureAwait(false), null);
        }, ct).ConfigureAwait(false);
    }

    public Task LogoutAsync(CloudPrincipal user, string refreshToken, CancellationToken ct = default)
    {
        var hash = SiteKeys.Hash(refreshToken ?? string.Empty);
        var now = time.GetUtcNow();
        return db.InTransactionAsync(async s =>
        {
            var family = await s.QuerySingleOrDefaultAsync<string>("SELECT family FROM refresh_tokens WHERE token_hash = @hash AND user_id = @UserId", new { hash, user.UserId }, ct).ConfigureAwait(false);
            if (family is not null) await s.ExecuteAsync("UPDATE refresh_tokens SET revoked_utc = @now WHERE family = @family AND revoked_utc IS NULL", new { now, family }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, user.Username, "Logout", "Signed out", null, ct).ConfigureAwait(false);
            return 0;
        }, ct);
    }

    /// <summary>Changes the password, ends every other session and returns fresh tokens for this one.</summary>
    public async Task<AuthOutcome> ChangePasswordAsync(CloudPrincipal user, ChangePasswordRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var u = await CloudStore.UserAsync(s, user.UserId, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("Not signed in.");
            if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, u.PasswordHash)) return new AuthOutcome(null, "The current password is not correct.");
            var problems = options.PasswordPolicy.Check(request.NewPassword ?? string.Empty, u.Username);
            if (problems.Count > 0) return new AuthOutcome(null, string.Join(' ', problems));
            if (PasswordHasher.Verify(request.NewPassword!, u.PasswordHash)) return new AuthOutcome(null, "The new password must be different from the current one.");
            u.PasswordHash = PasswordHasher.Hash(request.NewPassword!);
            u.MustChangePassword = false;
            await s.ExecuteAsync("UPDATE cloud_users SET password_hash = @PasswordHash, must_change_password = @MustChangePassword WHERE id = @Id", u, ct).ConfigureAwait(false);
            await s.ExecuteAsync("UPDATE refresh_tokens SET revoked_utc = @now WHERE user_id = @Id AND revoked_utc IS NULL", new { now, u.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, u.Username, "PasswordChanged", "Password changed; other sessions ended", ip, ct).ConfigureAwait(false);
            return new AuthOutcome(await IssueAsync(s, u, Guid.NewGuid().ToString("N"), now, ct).ConfigureAwait(false), null);
        }, ct).ConfigureAwait(false);
    }

    public MfaSetupResponse BeginMfaSetup(CloudPrincipal user)
    {
        var secret = Totp.NewSecret();
        var uri = Totp.ProvisioningUri("AWMS Cloud", user.Username, secret);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return new MfaSetupResponse(secret, uri, Convert.ToBase64String(png));
    }

    public async Task<string?> EnableMfaAsync(CloudPrincipal user, MfaEnableRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        byte[] decoded;
        try { decoded = Totp.Base32Decode(request.Secret ?? string.Empty); }
        catch (FormatException) { return "The MFA secret is not valid. Start the set-up again."; }
        if (decoded.Length < 16) return "The MFA secret is too short. Start the set-up again.";
        if (!Totp.Verify(request.Secret!, (request.Code ?? string.Empty).Trim(), now, null, out var step)) return "The code does not match. Check the time on your phone and try again.";
        await db.InTransactionAsync(async s =>
        {
            await s.ExecuteAsync("UPDATE cloud_users SET mfa_secret = @secret, mfa_last_step = @step WHERE id = @UserId", new { secret = box.Protect(request.Secret!), step, user.UserId }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, user.Username, "MfaEnabled", "Authenticator app registered", ip, ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> DisableMfaAsync(CloudPrincipal user, MfaDisableRequest request, string? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var u = await CloudStore.UserAsync(s, user.UserId, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("Not signed in.");
            if (!PasswordHasher.Verify(request.Password ?? string.Empty, u.PasswordHash)) return "The password is not correct.";
            if (u.MfaSecret is null) return null;
            if (options.RequireMfaForAdministrators && u.Role == Roles.Administrator) return "MFA is mandatory for administrators on this server.";
            if (!Totp.Verify(box.Unprotect(u.MfaSecret), (request.Code ?? string.Empty).Trim(), now, u.MfaLastStep, out _)) return "The authentication code is not valid.";
            await s.ExecuteAsync("UPDATE cloud_users SET mfa_secret = NULL, mfa_last_step = NULL WHERE id = @Id", new { u.Id }, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, u.Username, "MfaDisabled", "Authenticator app removed", ip, ct).ConfigureAwait(false);
            return (string?)null;
        }, ct).ConfigureAwait(false);
    }

    public Task<UserInfoDto> MeAsync(CloudPrincipal user, CancellationToken ct = default) => db.ReadAsync(async s =>
    {
        var u = await CloudStore.UserAsync(s, user.UserId, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("Not signed in.");
        return await UserInfoAsync(s, u, ct).ConfigureAwait(false);
    }, ct);

    /// <summary>
    /// Runs after the token signature and lifetime are validated: re-reads the user so that disabling an account, a role
    /// change or a site re-assignment takes effect immediately, and builds the permission claims.
    /// </summary>
    public async Task EnrichAsync(TokenValidatedContext context)
    {
        if (!long.TryParse(context.Principal?.FindFirst(CloudClaims.Subject)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            context.Fail("Invalid token subject.");
            return;
        }
        var ct = context.HttpContext.RequestAborted;
        var (user, sites) = await db.ReadAsync(async s =>
        {
            var u = await CloudStore.UserAsync(s, id, ct).ConfigureAwait(false);
            return (u, u is null ? [] : await CloudStore.UserSitesAsync(s, u.Id, ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);
        if (user is null || !user.IsActive || user.LockedUntilUtc > time.GetUtcNow())
        {
            context.Fail("The account is disabled or locked.");
            return;
        }
        var claims = new List<Claim>
        {
            new(CloudClaims.Subject, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(CloudClaims.Name, user.Username),
            new(CloudClaims.DisplayName, user.DisplayName),
            new(CloudClaims.Role, user.Role),
        };
        claims.AddRange(PermissionsOf(user.Role).Select(p => new Claim(CloudClaims.Permission, p)));
        if (user.Role == Roles.Administrator) claims.Add(new Claim(CloudClaims.AllSites, "true"));
        else claims.AddRange(sites.Select(x => new Claim(CloudClaims.Site, x.SiteId.ToString(CultureInfo.InvariantCulture))));
        if (Restriction(user) is { } restriction) claims.Add(new Claim(CloudClaims.MustChangePassword, restriction));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme, CloudClaims.Name, CloudClaims.Role));
    }

    public static IReadOnlyList<string> PermissionsOf(string role) => Roles.Defaults.TryGetValue(role, out var p) ? p : [];

    private string? Restriction(CloudUserRow u) =>
        u.MustChangePassword ? RestrictionPassword
        : options.RequireMfaForAdministrators && u.Role == Roles.Administrator && u.MfaSecret is null ? RestrictionMfa
        : null;

    private async Task<LoginResponse> IssueAsync(DbSession s, CloudUserRow u, string family, DateTimeOffset now, CancellationToken ct)
    {
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.JwtIssuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(CloudClaims.Subject, u.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(CloudClaims.Name, u.Username),
                new Claim("jti", Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
        var refresh = SiteKeys.Base64Url(RandomNumberGenerator.GetBytes(32));
        await s.ExecuteAsync("""
            INSERT INTO refresh_tokens (user_id, token_hash, family, expires_utc, created_utc, replaced)
            VALUES (@userId, @hash, @family, @expiresUtc, @now, @replaced)
            """, new { userId = u.Id, hash = SiteKeys.Hash(refresh), family, expiresUtc = now.AddDays(options.RefreshTokenDays), now, replaced = false }, ct).ConfigureAwait(false);
        return new LoginResponse(token, expires, refresh, await UserInfoAsync(s, u, ct).ConfigureAwait(false));
    }

    private async Task<UserInfoDto> UserInfoAsync(DbSession s, CloudUserRow u, CancellationToken ct)
    {
        var sites = u.Role == Roles.Administrator
            ? (await CloudStore.SitesAsync(s, null, ct).ConfigureAwait(false)).Select(x => x.Code).ToList()
            : (await CloudStore.UserSitesAsync(s, u.Id, ct).ConfigureAwait(false)).Select(x => x.Code).ToList();
        var restriction = Restriction(u);
        return new UserInfoDto(u.Id, u.Username, u.DisplayName, u.Role, PermissionsOf(u.Role), sites, u.MfaSecret is not null,
            restriction == RestrictionPassword, restriction == RestrictionMfa);
    }

    private Task<CloudUserRow?> LockUserAsync(DbSession s, string username, CancellationToken ct) =>
        s.QuerySingleOrDefaultAsync<CloudUserRow>($"""
            SELECT id, username, display_name, password_hash, role, is_active, must_change_password, mfa_secret, mfa_last_step, failed_attempts,
                   locked_until_utc, last_login_utc, created_utc
            FROM cloud_users WHERE lower(username) = lower(@username){s.Dialect.ForUpdate}
            """, new { username }, ct);

    private async Task RegisterFailureAsync(DbSession s, CloudUserRow u, DateTimeOffset now, string? ip, string why, CancellationToken ct)
    {
        var attempts = u.FailedAttempts + 1;
        DateTimeOffset? lockUntil = attempts >= options.MaxFailedLogins ? now.AddMinutes(options.LockoutMinutes) : null;
        await s.ExecuteAsync("UPDATE cloud_users SET failed_attempts = @attempts, locked_until_utc = @lockUntil WHERE id = @Id",
            new { attempts = lockUntil is null ? attempts : 0, lockUntil, u.Id }, ct).ConfigureAwait(false);
        await CloudStore.ServerAuditAsync(s, now, u.Username, lockUntil is null ? "LoginFailed" : "AccountLocked",
            lockUntil is null ? why : $"{why}; locked for {options.LockoutMinutes} minutes after {attempts} failed attempts", ip, ct).ConfigureAwait(false);
    }
}
