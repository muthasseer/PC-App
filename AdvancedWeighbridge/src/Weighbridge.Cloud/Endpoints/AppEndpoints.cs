using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Endpoints;

/// <summary>Read and request endpoints for the web dashboard and the Android app.</summary>
public static class AppEndpoints
{
    public static void MapAppEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Api.Prefix);

        api.MapGet("/version", (TimeProvider time) => new VersionDto("AWMS Cloud", typeof(AppEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", Api.Version, time.GetUtcNow()));

        api.MapGet("/health", async (Database db, CancellationToken ct) =>
        {
            try
            {
                await db.ReadAsync(s => s.ExecuteScalarAsync<long>("SELECT 1", ct: ct), ct);
                return Results.Ok(new { status = "ok" });
            }
            catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
            {
                return Results.Json(new { status = "database unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapGet("/sites", (CloudQueryService q, HttpContext http, CancellationToken ct) => q.SitesAsync(http.User.CloudUser(), ct))
            .RequireAuthorization(Policies.Permission(Permissions.DashboardView));

        api.MapGet("/status", (string? site, CloudQueryService q, HttpContext http, CancellationToken ct) => q.StatusAsync(http.User.CloudUser(), site, ct))
            .RequireAuthorization(Policies.Permission(Permissions.DashboardView));

        api.MapGet("/transactions", (CloudQueryService queries, HttpContext http, string? site, DateTimeOffset? from, DateTimeOffset? to, string? q, string? status, string? vehicle,
                bool? manualOnly, int? page, int? pageSize, CancellationToken ct) =>
            queries.TransactionsAsync(http.User.CloudUser(), new TransactionQuery
            {
                Site = site, From = from, To = to, Q = q, Status = status, Vehicle = vehicle,
                ManualOnly = manualOnly ?? false, Page = page ?? 1, PageSize = pageSize ?? 50,
            }, ct))
            .RequireAuthorization(Policies.Permission(Permissions.HistoryView));

        api.MapGet("/transactions/{uid:guid}", (Guid uid, CloudQueryService q, HttpContext http, CancellationToken ct) => q.DetailAsync(http.User.CloudUser(), uid, ct))
            .RequireAuthorization(Policies.Permission(Permissions.HistoryView));

        api.MapGet("/transactions/{uid:guid}/ticket.pdf", async (Guid uid, CloudQueryService q, HttpContext http, CancellationToken ct) =>
        {
            var (pdf, name) = await q.TicketPdfAsync(http.User.CloudUser(), uid, ct);
            return Results.File(pdf, "application/pdf", name);
        }).RequireAuthorization(Policies.Permission(Permissions.HistoryView));

        api.MapPost("/transactions/{uid:guid}/cancel", (Guid uid, CancelRequest request, CloudQueryService q, HttpContext http, CancellationToken ct) =>
                q.RequestCancelAsync(http.User.CloudUser(), uid, request.Reason, http.Ip(), ct))
            .RequireAuthorization(Policies.Permission(Permissions.TransactionCancel));

        api.MapGet("/reports/summary", (CloudQueryService q, HttpContext http, TimeProvider time, string? groupBy, DateTimeOffset? from, DateTimeOffset? to, string? site, CancellationToken ct) =>
            {
                var (f, t) = Period(time, from, to);
                return q.SummaryAsync(http.User.CloudUser(), groupBy ?? "day", f, t, site, ct);
            })
            .RequireAuthorization(Policies.Permission(Permissions.ReportsView));

        api.MapGet("/reports/export", async (CloudQueryService q, HttpContext http, TimeProvider time, string? format, DateTimeOffset? from, DateTimeOffset? to, string? site, CancellationToken ct) =>
            {
                var (f, t) = Period(time, from, to);
                var (bytes, type, name) = await q.ExportAsync(http.User.CloudUser(), format ?? "csv", f, t, site, ct);
                return Results.File(bytes, type, name);
            })
            .RequireAuthorization(Policies.Permission(Permissions.ReportsExport));

        api.MapGet("/notifications", (CloudQueryService q, HttpContext http, long? afterId, bool? unreadOnly, int? limit, CancellationToken ct) =>
                q.NotificationsAsync(http.User.CloudUser(), afterId, unreadOnly ?? false, limit ?? 100, ct))
            .RequireAuthorization(Policies.Permission(Permissions.DashboardView));

        api.MapPost("/notifications/read", async (MarkReadRequest request, CloudQueryService q, HttpContext http, CancellationToken ct) =>
                Results.Ok(new { marked = await q.MarkReadAsync(http.User.CloudUser(), request.Ids, ct) }))
            .RequireAuthorization(Policies.Permission(Permissions.DashboardView));

        api.MapGet("/audit", (CloudQueryService queries, HttpContext http, string? site, DateTimeOffset? from, DateTimeOffset? to, string? q, string? category, int? page, int? pageSize,
                CancellationToken ct) => queries.AuditAsync(http.User.CloudUser(), site, from, to, q, category, page ?? 1, pageSize ?? 100, ct))
            .RequireAuthorization(Policies.Permission(Permissions.AuditView));

        api.MapGet("/audit/verify", (string site, CloudQueryService q, HttpContext http, CancellationToken ct) => q.VerifyAuditAsync(http.User.CloudUser(), site, ct))
            .RequireAuthorization(Policies.Permission(Permissions.AuditView));

        // Public: the QR code printed on tickets points here when the site is configured with the cloud verification address.
        api.MapGet("/verify", (string? t, string? u, string? n, CloudQueryService q, CancellationToken ct) => q.VerifyTicketAsync(t, u, n, ct))
            .RequireRateLimiting(RateLimits.Public);
        app.MapGet("/verify", async (string? t, string? u, string? n, CloudQueryService q, CancellationToken ct) =>
                Results.Content(VerifyPage.Render(await q.VerifyTicketAsync(t, u, n, ct)), "text/html; charset=utf-8"))
            .RequireRateLimiting(RateLimits.Public);
    }

    private static (DateTimeOffset From, DateTimeOffset To) Period(TimeProvider time, DateTimeOffset? from, DateTimeOffset? to)
    {
        var t = to ?? time.GetUtcNow();
        return (from ?? t.AddDays(-30), t);
    }
}
