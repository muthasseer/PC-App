using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;

namespace Weighbridge.Cloud.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup(Api.Prefix + "/auth").RequireRateLimiting(RateLimits.Login);

        auth.MapPost("/login", async (LoginRequest request, CloudAuthService service, HttpContext http, CancellationToken ct) =>
        {
            var r = await service.LoginAsync(request, http.Ip(), ct);
            return r.Response is not null ? Results.Ok(r.Response) : Http.Error(r.Error!, StatusCodes.Status401Unauthorized, r.MfaRequired ? true : null);
        });

        auth.MapPost("/refresh", async (RefreshRequest request, CloudAuthService service, HttpContext http, CancellationToken ct) =>
        {
            var r = await service.RefreshAsync(request.RefreshToken, http.Ip(), ct);
            return r.Response is not null ? Results.Ok(r.Response) : Http.Error(r.Error!, StatusCodes.Status401Unauthorized);
        });

        auth.MapPost("/logout", async (LogoutRequest request, CloudAuthService service, HttpContext http, CancellationToken ct) =>
        {
            await service.LogoutAsync(http.User.CloudUser(), request.RefreshToken, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.User);

        var me = app.MapGroup(Api.Prefix + "/me").RequireAuthorization(Policies.User);

        me.MapGet("", (CloudAuthService service, HttpContext http, CancellationToken ct) => service.MeAsync(http.User.CloudUser(), ct));

        me.MapPost("/password", async (ChangePasswordRequest request, CloudAuthService service, HttpContext http, CancellationToken ct) =>
        {
            var r = await service.ChangePasswordAsync(http.User.CloudUser(), request, http.Ip(), ct);
            return r.Response is not null ? Results.Ok(r.Response) : Http.Error(r.Error!);
        }).RequireRateLimiting(RateLimits.Login);

        me.MapPost("/mfa/setup", (CloudAuthService service, HttpContext http) => Results.Ok(service.BeginMfaSetup(http.User.CloudUser())));

        me.MapPost("/mfa/enable", async (MfaEnableRequest request, CloudAuthService service, HttpContext http, CancellationToken ct) =>
        {
            var error = await service.EnableMfaAsync(http.User.CloudUser(), request, http.Ip(), ct);
            return error is null ? Results.NoContent() : Http.Error(error);
        }).RequireRateLimiting(RateLimits.Login);

        me.MapPost("/mfa/disable", async (MfaDisableRequest request, CloudAuthService service, HttpContext http, CancellationToken ct) =>
        {
            var error = await service.DisableMfaAsync(http.User.CloudUser(), request, http.Ip(), ct);
            return error is null ? Results.NoContent() : Http.Error(error);
        }).RequireRateLimiting(RateLimits.Login);
    }
}
