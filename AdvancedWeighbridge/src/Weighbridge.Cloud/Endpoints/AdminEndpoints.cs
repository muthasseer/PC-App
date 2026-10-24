using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;

namespace Weighbridge.Cloud.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var sites = app.MapGroup(Api.Prefix + "/admin/sites").RequireAuthorization(Policies.Permission(Permissions.CloudManage));
        sites.MapGet("", (CloudAdminService admin, CancellationToken ct) => admin.SitesAsync(ct));
        sites.MapPost("", (CreateSiteRequest request, CloudAdminService admin, HttpContext http, CancellationToken ct) => admin.CreateSiteAsync(http.User.CloudUser(), request, http.Ip(), ct));
        sites.MapPost("/{code}/rotate-key", (string code, CloudAdminService admin, HttpContext http, CancellationToken ct) => admin.RotateKeyAsync(http.User.CloudUser(), code, http.Ip(), ct));
        sites.MapPost("/{code}/activate", (string code, CloudAdminService admin, HttpContext http, CancellationToken ct) => admin.SetSiteActiveAsync(http.User.CloudUser(), code, true, http.Ip(), ct));
        sites.MapPost("/{code}/deactivate", (string code, CloudAdminService admin, HttpContext http, CancellationToken ct) => admin.SetSiteActiveAsync(http.User.CloudUser(), code, false, http.Ip(), ct));

        var review = app.MapGroup(Api.Prefix + "/admin").RequireAuthorization(Policies.Permission(Permissions.CloudManage));
        review.MapGet("/conflicts", (CloudAdminService admin, CancellationToken ct) => admin.ConflictsAsync(ct));
        review.MapGet("/server-audit", (int? limit, CloudAdminService admin, CancellationToken ct) => admin.ServerAuditAsync(limit ?? 200, ct));

        var users = app.MapGroup(Api.Prefix + "/admin/users").RequireAuthorization(Policies.Permission(Permissions.UsersManage));
        users.MapGet("", (CloudAdminService admin, CancellationToken ct) => admin.UsersAsync(ct));
        users.MapPost("", (CreateUserRequest request, CloudAdminService admin, HttpContext http, CancellationToken ct) => admin.CreateUserAsync(http.User.CloudUser(), request, http.Ip(), ct));
        users.MapPut("/{id:long}", (long id, UpdateUserRequest request, CloudAdminService admin, HttpContext http, CancellationToken ct) =>
            admin.UpdateUserAsync(http.User.CloudUser(), id, request, http.Ip(), ct));
        users.MapPost("/{id:long}/reset-password", async (long id, ResetPasswordRequest request, CloudAdminService admin, HttpContext http, CancellationToken ct) =>
        {
            await admin.ResetPasswordAsync(http.User.CloudUser(), id, request, http.Ip(), ct);
            return Results.NoContent();
        });
        users.MapPost("/{id:long}/unlock", async (long id, CloudAdminService admin, HttpContext http, CancellationToken ct) =>
        {
            await admin.UnlockAsync(http.User.CloudUser(), id, http.Ip(), ct);
            return Results.NoContent();
        });
        users.MapPost("/{id:long}/reset-mfa", async (long id, CloudAdminService admin, HttpContext http, CancellationToken ct) =>
        {
            await admin.ResetMfaAsync(http.User.CloudUser(), id, http.Ip(), ct);
            return Results.NoContent();
        });
        app.MapGet(Api.Prefix + "/admin/site-users", (CloudAdminService admin, CancellationToken ct) => admin.SiteUsersAsync(ct))
            .RequireAuthorization(Policies.Permission(Permissions.UsersManage));
    }
}
