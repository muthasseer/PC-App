using System.Net;
using System.Text;
using System.Text.Json;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Sync;
using Weighbridge.Contracts;
using Weighbridge.Core.Automation;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Devices.IO;

namespace Weighbridge.Application.Tests;

/// <summary>
/// In-memory stand-in for the AWMS cloud API. The sync service's background loop calls it concurrently with the
/// test, so every collection is guarded and the public properties return snapshots.
/// </summary>
public sealed class FakeCloud : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly List<(string Path, string Body, string? Key)> _requests = [];
    private readonly List<RemoteCommandDto> _commands = [];
    private readonly List<(long Id, CommandAck Ack)> _acks = [];
    private readonly HashSet<string> _transactionUids = [];

    public bool Online { get; set; } = true;
    public string? ConflictUid { get; set; }
    public IReadOnlyList<(string Path, string Body, string? Key)> Requests { get { lock (_lock) return [.. _requests]; } }
    public IReadOnlyList<(long Id, CommandAck Ack)> Acks { get { lock (_lock) return [.. _acks]; } }
    public IReadOnlyCollection<string> TransactionUids { get { lock (_lock) return [.. _transactionUids]; } }

    public void AddCommand(RemoteCommandDto command)
    {
        lock (_lock) _commands.Add(command);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Online) throw new HttpRequestException("Network unreachable");
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var key = request.Headers.TryGetValues(Api.SiteKeyHeader, out var k) ? k.First() : null;
        lock (_lock) _requests.Add((path, body, key));
        object? reply = null;
        if (path.EndsWith("/sync/heartbeat", StringComparison.Ordinal))
        {
            lock (_lock) reply = new HeartbeatResponse(DateTimeOffset.UtcNow, _commands.Count);
        }
        else if (path.EndsWith("/sync/commands", StringComparison.Ordinal))
        {
            lock (_lock) reply = _commands.ToList();
        }
        else if (path.Contains("/sync/commands/", StringComparison.Ordinal))
        {
            var id = long.Parse(path.Split('/')[^2]);
            var ack = JsonSerializer.Deserialize<CommandAck>(body, Api.Json)!;
            lock (_lock)
            {
                _acks.Add((id, ack));
                _commands.RemoveAll(c => c.Id == id);
            }
            reply = new { ok = true };
        }
        else if (path.EndsWith("/sync/users", StringComparison.Ordinal)) reply = new SyncBatchResult([]);
        else
        {
            using var doc = JsonDocument.Parse(body);
            var results = new List<SyncItemResult>();
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var uid = item.GetProperty("uid").GetString()!;
                if (path.EndsWith("/sync/transactions", StringComparison.Ordinal))
                {
                    lock (_lock) _transactionUids.Add(uid);
                }
                results.Add(uid == ConflictUid ? new SyncItemResult(uid, SyncItemStatus.Conflict, 9, "Different content at the same version") : new SyncItemResult(uid, SyncItemStatus.Accepted, null, null));
            }
            reply = new SyncBatchResult(results);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(reply, Api.Json), Encoding.UTF8, "application/json") };
    }
}

public class CloudSyncTests
{
    [Theory]
    [InlineData("https://cloud.example.lk", true)]
    [InlineData("http://cloud.example.lk", false)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("ftp://x", false)]
    [InlineData("not a url", false)]
    public void Only_https_is_accepted(string url, bool ok)
    {
        Assert.Equal(ok, CloudSyncService.ValidateUrl(new CloudSettings { BaseUrl = url }).Error is null);
    }

    [Fact]
    public async Task Offline_queue_resumes_and_remote_cancel_is_executed()
    {
        var cloud = new FakeCloud();
        await using var t = await TestHost.CreateAsync(cloud: cloud);
        await t.Host.Settings.SaveAsync(SettingKeys.Cloud, new CloudSettings { Enabled = true, BaseUrl = "https://cloud.test/", SiteKeyProtected = t.Host.Settings.Protector.Protect("site-key-123") }, t.Admin);
        await t.Host.StartDevicesAsync(t.Admin);
        var sync = t.Host.Cloud!;

        // Local weighing works while the cloud is down; changes wait in the outbox.
        cloud.Online = false;
        var first = await t.CaptureAsync(t.Operator, "SYNC-1", 11000);
        await t.CaptureAsync(t.Operator, "SYNC-1", 31000);
        var offline = await sync.RunOnceAsync();
        Assert.False(offline.Reachable);
        Assert.True(offline.Pending + offline.Failed >= 1);

        // Back online: the next run after the back-off uploads everything.
        cloud.Online = true;
        t.Advance(TimeSpan.FromMinutes(2));
        var online = await sync.RunOnceAsync();
        Assert.True(online.Reachable, online.LastError);
        Assert.Equal(0, online.Pending + online.Failed);
        Assert.Contains(first.Transaction!.Uid.ToString("D"), cloud.TransactionUids);
        Assert.All(cloud.Requests, r => Assert.Equal("site-key-123", r.Key));
        Assert.Contains(cloud.Requests, r => r.Path.EndsWith("/sync/audit") && r.Body.Contains("\"hash\""));
        var users = cloud.Requests.Single(r => r.Path.EndsWith("/sync/users")).Body;
        Assert.Contains("\"admin\"", users);
        Assert.DoesNotContain("PBKDF2", users); // never send password hashes
        var heartbeat = cloud.Requests.First(r => r.Path.EndsWith("/sync/heartbeat")).Body;
        Assert.Contains("\"siteCode\":\"QRY\"", heartbeat);

        // A cancellation requested by an authorised cloud user is applied locally and acknowledged.
        var second = (await t.CaptureAsync(t.Operator, "SYNC-2", 9000)).Transaction!;
        cloud.AddCommand(new RemoteCommandDto(41, Guid.NewGuid(), CommandTypes.CancelTransaction, second.Uid.ToString("D"), "Wrong customer", "manager@hq", DateTimeOffset.UtcNow, "Pending", null));
        await sync.RunOnceAsync();
        var ack = Assert.Single(cloud.Acks);
        Assert.True(ack.Ack.Success, ack.Ack.Message);
        var cancelled = await t.Host.Database.ReadAsync(s => TransactionStore.GetAsync(s, second.Id));
        Assert.Equal(TransactionStatus.Cancelled, cancelled!.Status);
        Assert.Contains("manager@hq", cancelled.CancelReason);

        // Conflicts are recorded for review, never silently overwritten.
        var third = (await t.CaptureAsync(t.Operator, "SYNC-3", 9500)).Transaction!;
        cloud.ConflictUid = third.Uid.ToString("D");
        var withConflict = await sync.RunOnceAsync();
        Assert.Equal(1, withConflict.Conflicts);
        Assert.True((await t.Host.Audit.VerifyAsync()).Valid);
    }
}

public class AutomaticModeEndToEndTests
{
    /// <summary>
    /// Real-time run with the built-in simulator: simulated indicator, ANPR, presence/position sensors, lights,
    /// barriers. A loaded truck drives on, is identified and captured automatically, leaves through the opened
    /// exit, returns empty and the ticket completes – without any operator action.
    /// </summary>
    [Fact]
    public async Task Simulated_truck_is_weighed_twice_without_operator()
    {
        await using var t = await TestHost.CreateAsync(fakeTime: false);
        await t.Host.Settings.SaveAsync(SettingKeys.Hardware, HardwareConfig.CreateSimulated(), t.Admin);
        var weighing = await t.Host.Settings.GetAsync<WeighingSettings>(SettingKeys.Weighing);
        await t.Host.Settings.SaveAsync(SettingKeys.Weighing, weighing with { DefaultMode = OperatingMode.Automatic }, t.Admin);
        await t.Host.StartDevicesAsync(t.Admin);
        var automation = t.Host.Automation!;
        Assert.Equal(OperatingMode.Automatic, automation.Mode);
        var world = t.Host.Simulator;
        var truck = world.Fleet[0];
        var results = new List<CaptureResultSummary>();
        automation.AutoCaptured += (_, r) => { lock (results) results.Add(new CaptureResultSummary(r.Success, r.IsFinal, r.Message, r.Transaction?.Id)); };

        await WaitAsync(() => t.Host.Indicator.Snapshot.Connection == Core.Indicators.ConnectionState.Connected, 30, "indicator connected");
        await WaitAsync(() => automation.Status.State == AutomationState.Idle && automation.Status.EntryLight == LightColor.Green, 30, "idle with green entry light");

        world.DriveOn(truck, loaded: true);
        await WaitAsync(() => { lock (results) return results.Count >= 1; }, 90, "first automatic capture");
        lock (results) Assert.True(results[0].Success, results[0].Message);
        await WaitAsync(() => automation.Status.ExitOpen, 30, "exit barrier opened");
        // The engine reports the barrier as open when it issues the command; the (simulated) I/O output follows.
        await WaitAsync(() => world.ExitBarrierOpen, 5, "exit barrier output switched on");
        world.DriveOff();
        await WaitAsync(() => automation.Status.State == AutomationState.Idle, 45, "vehicle left");
        await WaitAsync(() => !world.ExitBarrierOpen, 5, "exit barrier output switched off");

        world.DriveOn(truck, loaded: false);
        await WaitAsync(() => { lock (results) return results.Count >= 2; }, 90, "second automatic capture");
        CaptureResultSummary final;
        lock (results) final = results[1];
        Assert.True(final.Success, final.Message);
        Assert.True(final.IsFinal);
        world.DriveOff();

        var txn = await t.Host.Database.ReadAsync(s => TransactionStore.GetAsync(s, final.TransactionId!.Value));
        Assert.Equal(TransactionStatus.Completed, txn!.Status);
        Assert.Equal(OperatingMode.Automatic, txn.OperatingMode);
        Assert.Equal(truck.Registration, txn.VehicleReg);
        Assert.InRange(txn.Net!.Value.Kilograms, truck.LoadKg - 40, truck.LoadKg + 40);
        var audit = await t.Host.Audit.QueryAsync(t.Admin, new AuditFilter { Category = "Automation", PageSize = 500 });
        // The ANPR read arrives before the weight rises, so it is attached when the vehicle is detected.
        Assert.Contains(audit.Items, e => e.Action == "VehicleDetected" && e.Summary.Contains(truck.Registration));
        Assert.Contains(audit.Items, e => e.Action == "BarrierOpened");
        Assert.True((await t.Host.Audit.VerifyAsync()).Valid);
    }

    private sealed record CaptureResultSummary(bool Success, bool IsFinal, string Message, long? TransactionId);

    private static async Task WaitAsync(Func<bool> condition, int seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(100);
        }
    }
}
