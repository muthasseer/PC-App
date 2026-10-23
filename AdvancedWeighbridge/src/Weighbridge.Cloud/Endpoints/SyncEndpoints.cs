using System.Text.Json;
using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;

namespace Weighbridge.Cloud.Endpoints;

/// <summary>Endpoints called by the desktop application (authenticated with the site key).</summary>
public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup(Api.Prefix + "/sync").RequireAuthorization(Policies.Site).RequireRateLimiting(RateLimits.Sync);

        sync.MapPost("/heartbeat", (HeartbeatRequest request, SyncIngestService service, HttpContext http, CancellationToken ct) =>
            service.HeartbeatAsync(http.User.Site(), request, ct));

        sync.MapPost("/transactions", (JsonElement body, SyncIngestService service, HttpContext http, CancellationToken ct) =>
            service.TransactionsAsync(http.User.Site(), body, ct));

        sync.MapPost("/notifications", (SyncBatch<SyncNotificationDto> batch, SyncIngestService service, HttpContext http, CancellationToken ct) =>
            service.NotificationsAsync(http.User.Site(), batch, ct));

        sync.MapPost("/audit", (SyncBatch<SyncAuditEventDto> batch, SyncIngestService service, HttpContext http, CancellationToken ct) =>
            service.AuditAsync(http.User.Site(), batch, ct));

        sync.MapPost("/users", (SyncBatch<SiteUserDto> batch, SyncIngestService service, HttpContext http, CancellationToken ct) =>
            service.UsersAsync(http.User.Site(), batch, ct));

        sync.MapGet("/commands", (SyncIngestService service, HttpContext http, CancellationToken ct) => service.CommandsAsync(http.User.Site(), ct));

        sync.MapPost("/commands/{id:long}/ack", async (long id, CommandAck ack, SyncIngestService service, HttpContext http, CancellationToken ct) =>
            await service.AckCommandAsync(http.User.Site(), id, ack, ct) ? Results.Ok(new { ok = true }) : Http.Error("Command not found for this site.", StatusCodes.Status404NotFound));
    }
}
