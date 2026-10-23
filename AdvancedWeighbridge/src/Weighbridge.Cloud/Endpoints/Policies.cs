using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Weighbridge.Cloud.Services;
using Weighbridge.Core.Security;

namespace Weighbridge.Cloud.Endpoints;

public static class Policies
{
    /// <summary>Any signed-in user, including one who still has to change the password or set up MFA.</summary>
    public const string User = "user";

    /// <summary>A site authenticated by its site key.</summary>
    public const string Site = "site";

    public static string Permission(string permission) => "perm:" + permission;

    public static void Register(AuthorizationOptions o)
    {
        o.AddPolicy(User, b => b.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().RequireClaim(CloudClaims.Subject));
        o.AddPolicy(Site, b => b.AddAuthenticationSchemes(SiteKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireClaim(CloudClaims.SiteId));
        foreach (var p in Permissions.All)
        {
            o.AddPolicy(Permission(p), b => b.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(CloudClaims.Permission, p)
                .RequireAssertion(c => !c.User.HasClaim(x => x.Type == CloudClaims.MustChangePassword)));
        }
    }
}

public static class RateLimits
{
    public const string Login = "login";
    public const string Public = "public";
    public const string Sync = "sync";
}
