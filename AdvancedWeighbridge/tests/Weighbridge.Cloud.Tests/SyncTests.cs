using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;

namespace Weighbridge.Cloud.Tests;

public class SyncTests
{
    internal static SyncTransactionDto Tx(Guid uid, int version, string status, long? gross, long? tare, string site = "QRY", string ticket = "QRY-000001", string vehicle = "WP CAB-1234",
        bool manual = false, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        long? net = gross is { } g && tare is { } t ? g - t : null;
        var measurements = new List<SyncMeasurementDto>
        {
            new(Guid.NewGuid(), 1, manual ? "Manual" : "Indicator", gross ?? tare ?? 0, null, "kg", !manual, false, null, manual ? "Indicator failure" : null, when.AddMinutes(-20), 120,
                "ST,GS,+031000.0kg", "Test profile", 1, "SemiAutomatic", "op", null, null),
        };
        if (status == "Completed") measurements.Add(new(Guid.NewGuid(), 2, "Indicator", tare ?? 0, null, "kg", true, false, null, null, when, 90, "ST,GS,+011000.0kg", "Test profile", 1, "SemiAutomatic", "op", null, null));
        return new SyncTransactionDto(uid, ticket, version, status, "Outbound", "SemiAutomatic", site, "WB1", vehicle, null, "C1", "Builder Ltd", null, null, "SAND", "River sand",
            null, null, null, null, gross, status == "Completed" ? tare : null, status == "Completed" ? gross : null, status == "Completed" ? tare : null, status == "Completed" ? net : null,
            true, status == "Completed" ? "Measured" : null, manual, false, "op", when.AddMinutes(-20), status == "Completed" ? "op" : null, status == "Completed" ? when : null,
            status == "Cancelled" ? "mgr" : null, status == "Cancelled" ? when : null, status == "Cancelled" ? "Wrong vehicle" : null, when, null, measurements);
    }

    private static async Task<SyncBatchResult> PushAsync(HttpClient site, params SyncTransactionDto[] items)
    {
        var response = await site.PostAsJsonAsync("/api/v1/sync/transactions", new SyncBatch<SyncTransactionDto>(items), Api.Json);
        Assert.True(response.IsSuccessStatusCode, await CloudFactory.ErrorAsync(response));
        return (await response.Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json))!;
    }

    internal static HeartbeatRequest Heartbeat(string code, DateTimeOffset now, long? liveG = 31_000_000) => new(code, "Quarry", "1.0.0", now,
        [new WeighbridgeStatusDto("WB1", "Bridge 1", "Connected", liveG, true, now, "Stable", "SemiAutomatic", null, null, 1)],
        new SystemHealthDto(120.5, now.AddHours(-3), 0, 0, "Sqlite", []));

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Site_key_authenticates_and_identifies_the_site(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var key = await f.CreateSiteAsync(admin, "QRY", "Quarry");
        Assert.StartsWith("awms_", key);

        var none = await f.Anonymous().PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("QRY", f.Time.GetUtcNow()), Api.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        var bad = await f.WithSiteKey("awms_wrong").PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("QRY", f.Time.GetUtcNow()), Api.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var badBody = await bad.Content.ReadAsStringAsync();
        Assert.True(badBody.Contains("Invalid site key"), badBody);

        // A user token is not a site key and vice versa.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/sync/commands")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.WithSiteKey(key).GetAsync("/api/v1/sites")).StatusCode);

        var mismatch = await f.WithSiteKey(key).PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("OTHER", f.Time.GetUtcNow()), Api.Json);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Contains("belongs to site 'QRY'", await CloudFactory.ErrorAsync(mismatch));

        var ok = await f.WithSiteKey(key).PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("qry", f.Time.GetUtcNow()), Api.Json);
        Assert.True(ok.IsSuccessStatusCode, await CloudFactory.ErrorAsync(ok));

        // Rotating the key invalidates the old one at once.
        var rotated = await (await admin.PostAsync("/api/v1/admin/sites/QRY/rotate-key", null)).Content.ReadFromJsonAsync<CreateSiteResponse>(Api.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.WithSiteKey(key).GetAsync("/api/v1/sync/commands")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.WithSiteKey(rotated!.SiteKey).GetAsync("/api/v1/sync/commands")).StatusCode);

        // Deactivated sites are refused.
        await admin.PostAsync("/api/v1/admin/sites/QRY/deactivate", null);
        var off = await f.WithSiteKey(rotated.SiteKey).GetAsync("/api/v1/sync/commands");
        Assert.Equal(HttpStatusCode.Unauthorized, off.StatusCode);
        Assert.Contains("deactivated", await CloudFactory.ErrorAsync(off));

        // Only the hash of the key is stored.
        var db = f.Services.GetRequiredService<Weighbridge.Data.Database>();
        var stored = await db.ReadAsync(s => s.QuerySingleAsync<string>("SELECT key_hash FROM sites WHERE code = 'QRY'"));
        Assert.Equal(SiteKeys.Hash(rotated.SiteKey), stored);
        Assert.DoesNotContain(rotated.SiteKey, stored);
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Versioned_transactions_are_never_silently_overwritten(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var site = f.WithSiteKey(await f.CreateSiteAsync(admin, "QRY", "Quarry"));
        await f.CreateSiteAsync(admin, "CMB", "Colombo");
        var uid = Guid.NewGuid();

        var pending = Tx(uid, 1, "Pending", 31_000_000, null);
        var r = await PushAsync(site, pending);
        Assert.Equal(SyncItemStatus.Accepted, Assert.Single(r.Results).Status);
        Assert.Equal(SyncItemStatus.Unchanged, (await PushAsync(site, pending)).Results[0].Status);

        var completed = Tx(uid, 2, "Completed", 31_000_000, 11_000_000);
        Assert.Equal(SyncItemStatus.Accepted, (await PushAsync(site, completed)).Results[0].Status);
        Assert.Equal(SyncItemStatus.Unchanged, (await PushAsync(site, completed)).Results[0].Status);

        var stale = (await PushAsync(site, Tx(uid, 1, "Pending", 31_000_000, null))).Results[0];
        Assert.Equal(SyncItemStatus.Stale, stale.Status);
        Assert.Equal(2, stale.ServerVersion);

        var conflict = (await PushAsync(site, completed with { Remarks = "edited elsewhere" })).Results[0];
        Assert.Equal(SyncItemStatus.Conflict, conflict.Status);

        // Validation: each bad item is rejected on its own; the good one in the same batch is stored.
        var other = Guid.NewGuid();
        var batch = await PushAsync(site,
            Tx(Guid.NewGuid(), 1, "Completed", 31_000_000, 11_000_000) with { NetG = 19_000_000 },
            Tx(Guid.NewGuid(), 1, "Completed", 31_000_000, 11_000_000, site: "CMB"),
            Tx(Guid.NewGuid(), 1, "Weird", 31_000_000, null),
            Tx(other, 1, "Completed", 25_000_000, 10_000_000, ticket: "QRY-000002", manual: true));
        Assert.Equal([SyncItemStatus.Rejected, SyncItemStatus.Rejected, SyncItemStatus.Rejected, SyncItemStatus.Accepted], batch.Results.Select(x => x.Status));
        Assert.Contains("net weight", batch.Results[0].Message);
        Assert.Contains("CMB", batch.Results[1].Message);

        // A malformed item does not break the batch either.
        using var raw = new StringContent("{\"items\":[{\"uid\":\"abc\",\"version\":\"x\"}]}", Encoding.UTF8, "application/json");
        var rawResult = await (await site.PostAsync("/api/v1/sync/transactions", raw)).Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json);
        Assert.Equal(SyncItemStatus.Rejected, Assert.Single(rawResult!.Results).Status);

        // The stored copy is the accepted version 2, and the conflict is listed for review.
        var detail = await admin.GetFromJsonAsync<TransactionDetailDto>($"/api/v1/transactions/{uid}", Api.Json);
        Assert.Equal(2, detail!.Transaction.Version);
        Assert.Null(detail.Transaction.Remarks);
        Assert.Equal(20_000_000, detail.Summary.NetG);
        var conflicts = await admin.GetFromJsonAsync<List<ConflictDto>>("/api/v1/admin/conflicts", Api.Json);
        Assert.Contains(conflicts!, c => c.Uid == uid.ToString("D") && c.ServerVersion == 2);

        var notifications = await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", Api.Json);
        Assert.Contains(notifications!, n => n.Title == "Weighment completed" && n.Message.Contains("QRY-000001") && n.Message.Contains("20,000 kg") && n.Severity == "Info");
        Assert.Contains(notifications!, n => n.Title == "Weighment completed" && n.Message.Contains("MANUAL") && n.Severity == "Warning");
        Assert.Contains(notifications!, n => n.Title == "Synchronisation conflict");
        Assert.Equal(2, notifications!.Count(n => n.Title == "Weighment completed"));
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Live_status_is_flagged_stale_and_offline_sites_are_reported(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var site = f.WithSiteKey(await f.CreateSiteAsync(admin, "QRY", "Quarry"));

        var before = Assert.Single((await admin.GetFromJsonAsync<List<SiteStatusDto>>("/api/v1/status", Api.Json))!);
        Assert.True(before.LiveDataStale);
        Assert.False(before.Site.Online);

        Assert.True((await site.PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("QRY", f.Time.GetUtcNow()), Api.Json)).IsSuccessStatusCode);
        var live = Assert.Single((await admin.GetFromJsonAsync<List<SiteStatusDto>>("/api/v1/status?site=QRY", Api.Json))!);
        Assert.False(live.LiveDataStale);
        Assert.True(live.Site.Online);
        Assert.Equal(31_000_000, live.Weighbridges[0].LiveG);
        Assert.Equal(120.5, live.Health!.DiskFreeGb);

        // Two minutes without a heartbeat: still "online", but the weight is no longer live.
        f.Time.Advance(TimeSpan.FromMinutes(2));
        var stale = Assert.Single((await admin.GetFromJsonAsync<List<SiteStatusDto>>("/api/v1/status", Api.Json))!);
        Assert.True(stale.LiveDataStale);
        Assert.True(stale.HeartbeatAgeSeconds >= 120);
        Assert.True(stale.Site.Online);

        // After the offline limit the monitor raises exactly one notification; the next heartbeat raises "back online".
        f.Time.Advance(TimeSpan.FromMinutes(5));
        var monitor = f.Services.GetRequiredService<SiteMonitor>();
        await monitor.CheckAsync(); // the background timer may already have run on the clock jump
        Assert.Equal(0, await monitor.CheckAsync());
        Assert.False(Assert.Single((await admin.GetFromJsonAsync<List<SiteStatusDto>>("/api/v1/status", Api.Json))!).Site.Online);
        Assert.True((await site.PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("QRY", f.Time.GetUtcNow()), Api.Json)).IsSuccessStatusCode);
        var notes = await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", Api.Json);
        Assert.Equal(1, notes!.Count(n => n.Title == "Site offline"));
        Assert.Contains(notes!, n => n.Title == "Site back online" && n.Message.Contains("without contact"));

        // Read state is per user.
        var unread = await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications?unreadOnly=true", Api.Json);
        Assert.NotEmpty(unread!);
        await admin.PostAsJsonAsync("/api/v1/notifications/read", new MarkReadRequest(null!), Api.Json);
        Assert.Empty((await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications?unreadOnly=true", Api.Json))!);
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Audit_chain_continuity_is_verified(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var site = f.WithSiteKey(await f.CreateSiteAsync(admin, "QRY", "Quarry"));
        var events = new List<SyncAuditEventDto>();
        var prev = SyncIngestService.Genesis;
        for (var i = 1; i <= 5; i++)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"event-{i}-{prev}"))).ToLowerInvariant();
            events.Add(new SyncAuditEventDto(Guid.NewGuid(), i, f.Time.GetUtcNow().AddMinutes(i), "op", "PC1", "Weighing", "Capture", "Transaction", null, $"Event {i}", null, prev, hash));
            prev = hash;
        }
        var result = await (await site.PostAsJsonAsync("/api/v1/sync/audit", new SyncBatch<SyncAuditEventDto>(events.Take(3).ToList()), Api.Json)).Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json);
        Assert.All(result!.Results, x => Assert.Equal(SyncItemStatus.Accepted, x.Status));
        var ok = await admin.GetFromJsonAsync<AuditContinuityDto>("/api/v1/audit/verify?site=QRY", Api.Json);
        Assert.True(ok!.Valid, ok.Message);
        Assert.Equal(3, ok.Checked);
        Assert.Contains("from the first event", ok.Message);

        // Event 4 never arrives (e.g. deleted before upload) → the chain is reported broken at event 5.
        await site.PostAsJsonAsync("/api/v1/sync/audit", new SyncBatch<SyncAuditEventDto>([events[4]]), Api.Json);
        var broken = await admin.GetFromJsonAsync<AuditContinuityDto>("/api/v1/audit/verify?site=QRY", Api.Json);
        Assert.False(broken!.Valid);
        Assert.Equal(5, broken.BrokenAtLocalId);

        // Re-sending an event with a different hash is a conflict; the original is kept.
        var altered = await (await site.PostAsJsonAsync("/api/v1/sync/audit", new SyncBatch<SyncAuditEventDto>([events[0] with { Hash = new string('a', 64) }]), Api.Json))
            .Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json);
        Assert.Equal(SyncItemStatus.Conflict, altered!.Results[0].Status);
        var page = await admin.GetFromJsonAsync<PagedDto<AuditEventDto>>("/api/v1/audit?site=QRY&q=event", Api.Json);
        Assert.Equal(4, page!.Total);
    }

    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Remote_cancellation_is_delivered_until_acknowledged(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var site = f.WithSiteKey(await f.CreateSiteAsync(admin, "QRY", "Quarry"));
        var uid = Guid.NewGuid();
        await PushAsync(site, Tx(uid, 2, "Completed", 31_000_000, 11_000_000));

        var noReason = await admin.PostAsJsonAsync($"/api/v1/transactions/{uid}/cancel", new CancelRequest(""), Api.Json);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var command = await (await admin.PostAsJsonAsync($"/api/v1/transactions/{uid}/cancel", new CancelRequest("Wrong customer selected"), Api.Json)).Content.ReadFromJsonAsync<RemoteCommandDto>(Api.Json);
        Assert.Equal("Pending", command!.Status);
        var again = await (await admin.PostAsJsonAsync($"/api/v1/transactions/{uid}/cancel", new CancelRequest("Wrong customer selected"), Api.Json)).Content.ReadFromJsonAsync<RemoteCommandDto>(Api.Json);
        Assert.Equal(command.Id, again!.Id);

        var hb = await (await site.PostAsJsonAsync("/api/v1/sync/heartbeat", Heartbeat("QRY", f.Time.GetUtcNow()), Api.Json)).Content.ReadFromJsonAsync<HeartbeatResponse>(Api.Json);
        Assert.Equal(1, hb!.PendingCommands);
        var delivered = await site.GetFromJsonAsync<List<RemoteCommandDto>>("/api/v1/sync/commands", Api.Json);
        Assert.Equal(uid.ToString("D"), Assert.Single(delivered!).TargetUid);
        // Not acknowledged yet (e.g. the response was lost): delivered again.
        Assert.Single((await site.GetFromJsonAsync<List<RemoteCommandDto>>("/api/v1/sync/commands", Api.Json))!);

        Assert.True((await site.PostAsJsonAsync($"/api/v1/sync/commands/{command.Id}/ack", new CommandAck(true, "Ticket QRY-000001 cancelled."), Api.Json)).IsSuccessStatusCode);
        Assert.Empty((await site.GetFromJsonAsync<List<RemoteCommandDto>>("/api/v1/sync/commands", Api.Json))!);
        var detail = await admin.GetFromJsonAsync<TransactionDetailDto>($"/api/v1/transactions/{uid}", Api.Json);
        Assert.Equal("Completed", Assert.Single(detail!.Commands).Status);

        // Commands of one site cannot be acknowledged with another site's key.
        var other = f.WithSiteKey(await f.CreateSiteAsync(admin, "CMB", "Colombo"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/sync/commands/{command.Id}/ack", new CommandAck(false, "x"), Api.Json)).StatusCode);
    }
}
