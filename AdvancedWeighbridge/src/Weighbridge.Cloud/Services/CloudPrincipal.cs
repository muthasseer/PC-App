using System.Globalization;
using System.Security.Claims;

namespace Weighbridge.Cloud.Services;

public static class CloudClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string DisplayName = "display_name";
    public const string Role = "role";
    public const string Permission = "perm";
    public const string Site = "site";
    public const string AllSites = "all_sites";
    public const string MustChangePassword = "must_change_password";
    public const string SiteId = "site_id";
    public const string SiteCode = "site_code";
}

/// <summary>The signed-in cloud user as resolved for the current request (role and site access are re-read on every request).</summary>
public sealed record CloudPrincipal(long UserId, string Username, string DisplayName, string Role, IReadOnlySet<string> Permissions, IReadOnlyList<long>? SiteIds)
{
    /// <summary>Null = all sites (administrators).</summary>
    public bool CanAccess(long siteId) => SiteIds is null || SiteIds.Contains(siteId);

    public static CloudPrincipal From(ClaimsPrincipal p)
    {
        var id = long.Parse(p.FindFirstValue(CloudClaims.Subject) ?? throw new UnauthorizedAccessException("Not signed in."), CultureInfo.InvariantCulture);
        var all = p.HasClaim(CloudClaims.AllSites, "true");
        return new CloudPrincipal(id, p.FindFirstValue(CloudClaims.Name) ?? string.Empty, p.FindFirstValue(CloudClaims.DisplayName) ?? string.Empty,
            p.FindFirstValue(CloudClaims.Role) ?? string.Empty,
            p.FindAll(CloudClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal),
            all ? null : p.FindAll(CloudClaims.Site).Select(c => long.Parse(c.Value, CultureInfo.InvariantCulture)).ToList());
    }
}

/// <summary>The site authenticated by its site key.</summary>
public sealed record SitePrincipal(long SiteId, string SiteCode)
{
    public static SitePrincipal From(ClaimsPrincipal p) => new(
        long.Parse(p.FindFirstValue(CloudClaims.SiteId) ?? throw new UnauthorizedAccessException("Site key required."), CultureInfo.InvariantCulture),
        p.FindFirstValue(CloudClaims.SiteCode) ?? string.Empty);
}
