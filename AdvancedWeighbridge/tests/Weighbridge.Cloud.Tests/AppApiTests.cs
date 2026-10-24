using System.Net;
using System.Net.Http.Json;
using System.Text;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;

namespace Weighbridge.Cloud.Tests;

public class AppApiTests
{
    private static async Task PushAsync(HttpClient site, params SyncTransactionDto[] items)
    {
        var response = await site.PostAsJsonAsync("/api/v1/sync/transactions", new SyncBatch<SyncTransactionDto>(items), Api.Json);
        var result = await response.Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json);
        Assert.All(result!.Results, r => Assert.Equal(SyncItemStatus.Accepted, r.Status));
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Users_see_only_their_sites_and_permissions_are_enforced(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var qry = f.WithSiteKey(await f.CreateSiteAsync(admin, "QRY", "Quarry"));
        var cmb = f.WithSiteKey(await f.CreateSiteAsync(admin, "CMB", "Colombo"));
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await PushAsync(qry, SyncTests.Tx(mine, 2, "Completed", 31_000_000, 11_000_000));
        await PushAsync(cmb, SyncTests.Tx(theirs, 2, "Completed", 28_000_000, 9_000_000, site: "CMB", ticket: "CMB-000001"));

        var viewer = await f.UserAsync(admin, "viewer1", Roles.Viewer, "QRY");
        var manager = await f.UserAsync(admin, "manager1", Roles.Manager, "QRY");

        var list = await viewer.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions", Api.Json);
        Assert.Equal(mine, Assert.Single(list!.Items).Uid);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/transactions/{theirs}")).StatusCode);
        Assert.Equal("QRY", Assert.Single((await viewer.GetFromJsonAsync<List<SiteDto>>("/api/v1/sites", Api.Json))!).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/status?site=CMB")).StatusCode);
        Assert.Equal(2, (await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions", Api.Json))!.Total);

        // Viewers read; they cannot cancel, export, see the audit trail or administer.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/transactions/{mine}/cancel", new CancelRequest("Should not work"), Api.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/reports/export?format=csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/v1/admin/sites")).StatusCode);

        // A manager may request a cancellation, but only for a site they can see.
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/v1/transactions/{mine}/cancel", new CancelRequest("Customer changed"), Api.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsJsonAsync($"/api/v1/transactions/{theirs}/cancel", new CancelRequest("Customer changed"), Api.Json)).StatusCode);

        // Disabling a user takes effect on the next request, without waiting for the token to expire.
        var users = await admin.GetFromJsonAsync<List<CloudUserDto>>("/api/v1/admin/users", Api.Json);
        var viewerId = users!.Single(u => u.Username == "viewer1").Id;
        Assert.True((await admin.PutAsJsonAsync($"/api/v1/admin/users/{viewerId}", new UpdateUserRequest(null, null, null, false), Api.Json)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/v1/transactions")).StatusCode);

        // Re-assigning sites is also immediate.
        Assert.True((await admin.PutAsJsonAsync($"/api/v1/admin/users/{users!.Single(u => u.Username == "manager1").Id}", new UpdateUserRequest(null, null, ["CMB"], null), Api.Json)).IsSuccessStatusCode);
        Assert.Equal(theirs, Assert.Single((await manager.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions", Api.Json))!.Items).Uid);

        // The last administrator cannot be removed.
        var adminId = users!.Single(u => u.Username == "admin").Id;
        var demote = await admin.PutAsJsonAsync($"/api/v1/admin/users/{adminId}", new UpdateUserRequest(null, Roles.Viewer, null, null), Api.Json);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Search_reports_exports_ticket_copy_and_public_verification(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var site = f.WithSiteKey(await f.CreateSiteAsync(admin, "QRY", "Quarry"));
        var now = f.Time.GetUtcNow();
        var a = Guid.NewGuid();
        await PushAsync(site,
            SyncTests.Tx(a, 2, "Completed", 31_000_000, 11_000_000, at: now.AddHours(-2)),
            SyncTests.Tx(Guid.NewGuid(), 2, "Completed", 25_500_000, 10_000_000, ticket: "QRY-000002", vehicle: "NC LK-5521", manual: true, at: now.AddHours(-1)),
            SyncTests.Tx(Guid.NewGuid(), 1, "Pending", 18_000_000, null, ticket: "QRY-000003", vehicle: "SP KA-7788"));

        Assert.Equal(1, (await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions?vehicle=nclk5521", Api.Json))!.Total);
        Assert.Equal(1, (await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions?q=KA-77", Api.Json))!.Total);
        Assert.Equal(1, (await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions?manualOnly=true", Api.Json))!.Total);
        Assert.Equal(1, (await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions?status=Pending", Api.Json))!.Total);
        var paged = await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions?pageSize=2&page=2", Api.Json);
        Assert.Equal(3, paged!.Total);
        Assert.Single(paged.Items);

        var from = Uri.EscapeDataString(now.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddMinutes(1).ToString("O"));
        var byVehicle = await admin.GetFromJsonAsync<SummaryReportDto>($"/api/v1/reports/summary?groupBy=vehicle&from={from}&to={to}", Api.Json);
        Assert.Equal(2, byVehicle!.TotalCount);
        Assert.Equal(20_000_000 + 15_500_000, byVehicle.TotalNetG);
        Assert.Equal("WP CAB-1234", byVehicle.Rows[0].Key); // largest net first
        var byDay = await admin.GetFromJsonAsync<SummaryReportDto>($"/api/v1/reports/summary?groupBy=day&from={from}&to={to}", Api.Json);
        Assert.Equal(2, byDay!.Rows.Sum(r => r.Count));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/reports/summary?groupBy=colour")).StatusCode);

        var csv = await admin.GetAsync($"/api/v1/reports/export?format=csv&from={from}&to={to}");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var csvText = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        Assert.Contains("QRY-000002", csvText);
        Assert.DoesNotContain("QRY-000003", csvText); // pending tickets are not completed weighments
        var xlsx = await admin.GetByteArrayAsync($"/api/v1/reports/export?format=xlsx&from={from}&to={to}");
        Assert.Equal("PK"u8.ToArray(), xlsx[..2]);
        var pdf = await admin.GetByteArrayAsync($"/api/v1/reports/export?format=pdf&from={from}&to={to}");
        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);

        var ticket = await admin.GetAsync($"/api/v1/transactions/{a}/ticket.pdf");
        Assert.Equal("application/pdf", ticket.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF"u8.ToArray(), (await ticket.Content.ReadAsByteArrayAsync())[..4]);

        // Public QR check: matches the stored record; a changed weight or unknown id does not.
        var anonymous = f.Anonymous();
        var good = await anonymous.GetFromJsonAsync<TicketVerifyDto>($"/api/v1/verify?t=QRY-000001&u={a:N}&n=20000000&s=SIG", Api.Json);
        Assert.True(good!.Valid, good.Message);
        var forged = await anonymous.GetFromJsonAsync<TicketVerifyDto>($"/api/v1/verify?t=QRY-000001&u={a:N}&n=29000000&s=SIG", Api.Json);
        Assert.False(forged!.Valid);
        var html = await anonymous.GetStringAsync($"/verify?t=%3Cscript%3E&u={Guid.NewGuid():N}&n=1&s=x");
        Assert.Contains("could not be confirmed", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task Security_headers_errors_and_health()
    {
        await using var f = await CloudFactory.CreateAsync();
        var anonymous = f.Anonymous();
        var health = await anonymous.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("default-src 'self'", health.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", health.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("no-store", health.Headers.CacheControl!.ToString());
        Assert.False(health.Headers.Contains("Server"));

        var version = await anonymous.GetFromJsonAsync<VersionDto>("/api/v1/version", Api.Json);
        Assert.Equal("v1", version!.ApiVersion);

        using var broken = new StringContent("{not json", Encoding.UTF8, "application/json");
        var bad = await anonymous.PostAsync("/api/v1/auth/login", broken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var index = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);

        // The dashboard must work under the strict CSP (no inline script or style) and never build HTML from data.
        var html = await index.Content.ReadAsStringAsync();
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
        Assert.DoesNotContain("style=", html);
        var js = await anonymous.GetStringAsync("/app.js");
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("insertAdjacentHTML", js);
        Assert.DoesNotContain("eval(", js);
        Assert.DoesNotContain("localStorage", js); // tokens never outlive the browser tab

        // Secrets are generated once and kept in a private key file next to the database.
        var keyFile = Path.Combine(f.DataDirectory, "cloud-keys.json");
        Assert.True(File.Exists(keyFile));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
    }
}
