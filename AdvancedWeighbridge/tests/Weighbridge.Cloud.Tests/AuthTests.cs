using System.Net;
using System.Net.Http.Json;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;

namespace Weighbridge.Cloud.Tests;

public class AuthTests
{
    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Bootstrap_admin_must_change_password_before_using_the_api(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var first = await f.LoginAsync("admin", CloudFactory.AdminPassword);
        Assert.True(first.User.MustChangePassword);
        Assert.Equal(Roles.Administrator, first.User.Role);

        var restricted = f.WithToken(first.AccessToken);
        var denied = await restricted.GetAsync("/api/v1/sites");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("Change your password", await CloudFactory.ErrorAsync(denied));
        Assert.Equal(HttpStatusCode.OK, (await restricted.GetAsync("/api/v1/me")).StatusCode);

        var weak = await restricted.PostAsJsonAsync("/api/v1/me/password", new ChangePasswordRequest(CloudFactory.AdminPassword, "short"), Api.Json);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var changed = await restricted.PostAsJsonAsync("/api/v1/me/password", new ChangePasswordRequest(CloudFactory.AdminPassword, CloudFactory.AdminCurrentPassword), Api.Json);
        Assert.True(changed.IsSuccessStatusCode, await CloudFactory.ErrorAsync(changed));
        var fresh = (await changed.Content.ReadFromJsonAsync<LoginResponse>(Api.Json))!;
        Assert.False(fresh.User.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await f.WithToken(fresh.AccessToken).GetAsync("/api/v1/sites")).StatusCode);

        // The password change ended the earlier session.
        var oldRefresh = await f.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken), Api.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Repeated_failures_lock_the_account(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var unknown = await f.PostLoginAsync("nobody", "whatever-123");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Contains("Invalid user name or password", await CloudFactory.ErrorAsync(unknown));

        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await f.PostLoginAsync("admin", "Wrong#Password1")).StatusCode);
        var locked = await f.PostLoginAsync("admin", CloudFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("locked", await CloudFactory.ErrorAsync(locked));

        f.Time.Advance(TimeSpan.FromMinutes(16));
        var ok = await f.LoginAsync("admin", CloudFactory.AdminPassword);
        Assert.Equal("admin", ok.User.Username);
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_session(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        await f.AdminAsync();
        var login = await f.LoginAsync("admin", CloudFactory.AdminCurrentPassword);

        var r1 = await f.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken), Api.Json);
        Assert.True(r1.IsSuccessStatusCode);
        var second = (await r1.Content.ReadFromJsonAsync<LoginResponse>(Api.Json))!;
        Assert.NotEqual(login.RefreshToken, second.RefreshToken);

        // The first token was rotated; presenting it again later means it was copied → the whole session ends.
        f.Time.Advance(TimeSpan.FromMinutes(1));
        var reuse = await f.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken), Api.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await f.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken), Api.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);

        // Access tokens expire.
        var client = f.WithToken(second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(20));
        var expired = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous().GetAsync("/api/v1/me")).StatusCode);

        var admin = await f.AdminAsync();
        var audit = await admin.GetFromJsonAsync<List<Services.ServerAuditDto>>("/api/v1/admin/server-audit", Api.Json);
        Assert.Contains(audit!, a => a.Action == "RefreshTokenReuse");
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Mfa_enrolment_requires_a_valid_code_and_codes_cannot_be_replayed(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var setup = await (await admin.PostAsync("/api/v1/me/mfa/setup", null)).Content.ReadFromJsonAsync<MfaSetupResponse>(Api.Json);
        Assert.StartsWith("otpauth://totp/", setup!.ProvisioningUri);
        Assert.True(Convert.FromBase64String(setup.QrPngBase64).AsSpan(1, 3).SequenceEqual("PNG"u8));

        var wrong = await admin.PostAsJsonAsync("/api/v1/me/mfa/enable", new MfaEnableRequest(setup.Secret, "000000"), Api.Json);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var code = Totp.Compute(setup.Secret, Totp.Step(f.Time.GetUtcNow()));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/me/mfa/enable", new MfaEnableRequest(setup.Secret, code), Api.Json)).StatusCode);

        var noCode = await f.PostLoginAsync("admin", CloudFactory.AdminCurrentPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Equal(true, (await noCode.Content.ReadFromJsonAsync<ErrorDto>(Api.Json))!.MfaRequired);

        // The code used for enrolment cannot be used again (replay protection); the next one works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.PostLoginAsync("admin", CloudFactory.AdminCurrentPassword, code)).StatusCode);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        var next = Totp.Compute(setup.Secret, Totp.Step(f.Time.GetUtcNow()));
        var ok = await f.LoginAsync("admin", CloudFactory.AdminCurrentPassword, next);
        Assert.True(ok.User.MfaEnabled);
    }
}
