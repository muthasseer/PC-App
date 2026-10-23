using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Weighbridge.Contracts;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

/// <summary>Authenticates a site by the key in the <c>X-AWMS-Site-Key</c> header (compared by SHA-256 hash).</summary>
public sealed class SiteKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Database db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SiteKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Api.SiteKeyHeader, out var values) || string.IsNullOrWhiteSpace(values.ToString())) return AuthenticateResult.NoResult();
        var key = values.ToString();
        if (key.Length > 200) return AuthenticateResult.Fail("Invalid site key.");
        var site = await db.ReadAsync(s => CloudStore.SiteByKeyHashAsync(s, SiteKeys.Hash(key), Context.RequestAborted)).ConfigureAwait(false);
        if (site is null) return AuthenticateResult.Fail("Invalid site key.");
        if (!site.IsActive) return AuthenticateResult.Fail("This site is deactivated on the cloud server.");
        var identity = new ClaimsIdentity(
            [
                new Claim(CloudClaims.SiteId, site.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(CloudClaims.SiteCode, site.Code),
                new Claim(CloudClaims.Name, "site:" + site.Code),
            ], SchemeName, CloudClaims.Name, CloudClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync().ConfigureAwait(false);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new ErrorDto(result.Failure?.Message ?? $"A valid site key is required in the {Api.SiteKeyHeader} header."), Api.Json).ConfigureAwait(false);
    }
}
