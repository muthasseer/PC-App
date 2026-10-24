using System.Net.Http.Json;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Tests;
using Weighbridge.Contracts;
using Weighbridge.Core.Weighing;
using Weighbridge.Data.Stores;

namespace Weighbridge.Cloud.Tests;

/// <summary>The real desktop application (in-memory) synchronising with the real cloud server (in-memory).</summary>
public class DesktopEndToEndTests
{
    [Theory]
    [MemberData(nameof(CloudFactory.Providers), MemberType = typeof(CloudFactory))]
    public async Task Desktop_uploads_weighments_and_applies_a_remote_cancellation(string provider)
    {
        await using var f = await CloudFactory.CreateAsync(provider);
        var admin = await f.AdminAsync();
        var key = await f.CreateSiteAsync(admin, "QRY", "Test Quarry");

        await using var desk = await TestHost.CreateAsync(cloud: f.Server.CreateHandler());
        await desk.Host.Settings.SaveAsync(SettingKeys.Cloud, new CloudSettings
        {
            Enabled = true, BaseUrl = "http://localhost/", SiteKeyProtected = desk.Host.Settings.Protector.Protect(key),
        }, desk.Admin);
        await desk.Host.StartDevicesAsync(desk.Admin);
        var sync = desk.Host.Cloud!;

        var first = (await desk.CaptureAsync(desk.Operator, "WP CAB-1234", 31000)).Transaction!;
        await desk.CaptureAsync(desk.Operator, "WP CAB-1234", 11000);
        var pending = (await desk.CaptureAsync(desk.Operator, "NC LK-5521", 24000)).Transaction!;

        var status = await sync.RunOnceAsync();
        Assert.True(status.Reachable, status.LastError);
        Assert.Equal(0, status.Pending + status.Failed + status.Conflicts);

        // The cloud has both transactions with the values computed at the site.
        var list = await admin.GetFromJsonAsync<PagedDto<TransactionSummaryDto>>("/api/v1/transactions", Api.Json);
        Assert.Equal(2, list!.Total);
        var done = list.Items.Single(t => t.Uid == first.Uid);
        Assert.Equal("Completed", done.Status);
        Assert.Equal(20_000_000, done.NetG);
        Assert.Equal("QRY", done.SiteCode);
        Assert.Equal("Pending", list.Items.Single(t => t.Uid == pending.Uid).Status);
        var detail = await admin.GetFromJsonAsync<TransactionDetailDto>($"/api/v1/transactions/{first.Uid}", Api.Json);
        Assert.Equal(2, detail!.Transaction.Measurements.Count);
        Assert.All(detail.Transaction.Measurements, m => Assert.Equal("Indicator", m.Source));

        var notes = await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", Api.Json);
        Assert.Contains(notes!, n => n.Title == "Weighment completed" && n.Message.Contains(first.TicketNo));

        var siteStatus = Assert.Single((await admin.GetFromJsonAsync<List<SiteStatusDto>>("/api/v1/status", Api.Json))!);
        Assert.True(siteStatus.Site.Online);
        Assert.Equal("WB1", Assert.Single(siteStatus.Weighbridges).Code);

        var audit = await admin.GetFromJsonAsync<AuditContinuityDto>("/api/v1/audit/verify?site=QRY", Api.Json);
        Assert.True(audit!.Valid, audit.Message);
        Assert.True(audit.Checked > 5);
        Assert.Contains("from the first event", audit.Message);

        var siteUsers = await admin.GetFromJsonAsync<List<SiteUserListDto>>("/api/v1/admin/site-users", Api.Json);
        Assert.Contains(siteUsers!, u => u.Username == "op" && u.SiteCode == "QRY");

        // A manager in head office cancels the completed ticket; the site applies it and reports back.
        var manager = await f.UserAsync(admin, "hq.manager", Core.Security.Roles.Manager, "QRY");
        var command = await (await manager.PostAsJsonAsync($"/api/v1/transactions/{first.Uid}/cancel", new CancelRequest("Duplicate ticket for the same load"), Api.Json))
            .Content.ReadFromJsonAsync<RemoteCommandDto>(Api.Json);
        Assert.Equal("Pending", command!.Status);

        await sync.RunOnceAsync(); // executes the command and acknowledges it
        var local = await desk.Host.Database.ReadAsync(s => TransactionStore.GetAsync(s, first.Id));
        Assert.Equal(TransactionStatus.Cancelled, local!.Status);
        Assert.Contains("hq.manager", local.CancelReason);
        await sync.RunOnceAsync(); // uploads the cancelled version

        var after = await admin.GetFromJsonAsync<TransactionDetailDto>($"/api/v1/transactions/{first.Uid}", Api.Json);
        Assert.Equal("Cancelled", after!.Summary.Status);
        Assert.True(after.Transaction.Version > detail.Transaction.Version);
        Assert.Equal("Completed", Assert.Single(after.Commands).Status);
        Assert.True((await desk.Host.Audit.VerifyAsync()).Valid);
        Assert.True((await admin.GetFromJsonAsync<AuditContinuityDto>("/api/v1/audit/verify?site=QRY", Api.Json))!.Valid);

        // A wrong key is reported to the operator in plain words; nothing is lost locally.
        await desk.Host.Settings.SaveAsync(SettingKeys.Cloud, new CloudSettings
        {
            Enabled = true, BaseUrl = "http://localhost/", SiteKeyProtected = desk.Host.Settings.Protector.Protect("awms_not-the-key"),
        }, desk.Admin);
        await desk.CaptureAsync(desk.Operator, "NC LK-5521", 9000);
        var refused = await sync.RunOnceAsync();
        Assert.False(refused.Reachable);
        Assert.Contains("Invalid site key", refused.LastError);
        Assert.True(refused.Pending + refused.Failed >= 1);
    }
}
