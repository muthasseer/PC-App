using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weighbridge.Application.Automation;
using Weighbridge.Application.Hardware;
using Weighbridge.Application.Maintenance;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Sync;

public sealed record CloudStatus(bool Enabled, bool Reachable, DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastAttemptUtc, string? LastError, long Pending, long Failed, long Conflicts);

/// <summary>
/// Optional cloud synchronisation. Local weighing never depends on it: changes are queued in the local outbox inside
/// the same database transaction and uploaded when the cloud is reachable, with exponential back-off.
/// </summary>
public sealed class CloudSyncService : IDisposable
{
    public const string ServiceUsername = "cloud-sync";
    private readonly Database _db;
    private readonly SettingsService _settings;
    private readonly WeighingService _weighing;
    private readonly IndicatorManager _indicator;
    private readonly Func<AutomationStatus?> _automation;
    private readonly HealthService _health;
    private readonly AuditService _audit;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _running = new(1, 1);
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastUsersSent = DateTimeOffset.MinValue;
    private TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CloudSyncService(Database db, SettingsService settings, WeighingService weighing, IndicatorManager indicator, Func<AutomationStatus?> automation,
        HealthService health, AuditService audit, TimeProvider time, ILogger<CloudSyncService> log, HttpMessageHandler? handler = null)
    {
        _db = db;
        _settings = settings;
        _weighing = weighing;
        _indicator = indicator;
        _automation = automation;
        _health = health;
        _audit = audit;
        _handler = handler;
        _time = time;
        _log = log;
        Status = new CloudStatus(false, false, null, null, null, 0, 0, 0);
    }

    public CloudStatus Status { get; private set; }
    public event EventHandler<CloudStatus>? StatusChanged;

    public static (Uri? Uri, string? Error) ValidateUrl(CloudSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.BaseUrl) || !Uri.TryCreate(s.BaseUrl.Trim(), UriKind.Absolute, out var uri)) return (null, "The cloud address is not a valid URL.");
        var local = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && (local || s.AllowInsecureHttp)))
        {
            return (null, "The cloud address must use HTTPS (encrypted connection).");
        }
        return (uri, null);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Requests an immediate synchronisation (e.g. right after a weighing).</summary>
    public void Kick() => _kick.TrySetResult();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cloud = await _settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
            if (cloud.Enabled)
            {
                try { await RunOnceAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "Cloud synchronisation failed");
                }
            }
            var kick = _kick;
            await Task.WhenAny(kick.Task, Task.Delay(TimeSpan.FromSeconds(Math.Max(5, cloud.IntervalSeconds)), _time, ct)).ConfigureAwait(false);
            if (kick.Task.IsCompleted) _kick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task<CloudStatus> RunOnceAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<CloudStatus> RunCoreAsync(CancellationToken ct)
    {
        var cloud = await _settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var counts = await _db.ReadAsync(s => SyncQueueStore.CountsAsync(s, ct), ct).ConfigureAwait(false);
        if (!cloud.Enabled) return Publish(new CloudStatus(false, false, Status.LastSuccessUtc, Status.LastAttemptUtc, null, counts.Pending, counts.Failed, counts.Conflicts));

        var (uri, error) = ValidateUrl(cloud);
        string? key = null;
        if (error is null)
        {
            try { key = cloud.SiteKeyProtected is null ? null : _settings.Protector.Unprotect(cloud.SiteKeyProtected); }
            catch (CryptographicException) { error = "The stored site key cannot be decrypted on this PC. Enter it again."; }
            if (key is null && error is null) error = "No site key is configured.";
        }
        if (error is not null) return Publish(Status with { Enabled = true, Reachable = false, LastAttemptUtc = now, LastError = error, Pending = counts.Pending, Failed = counts.Failed, Conflicts = counts.Conflicts });

        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.BaseAddress = new Uri(uri!.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Add(Api.SiteKeyHeader, key);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AWMS-Desktop", BackupService.AppVersion));

        try
        {
            await HeartbeatAsync(http, cloud, ct).ConfigureAwait(false);
            await UploadQueueAsync(http, ct).ConfigureAwait(false);
            if (cloud.UploadAudit) await UploadAuditAsync(http, ct).ConfigureAwait(false);
            if ((now - _lastUsersSent).TotalMinutes >= 15) await UploadUsersAsync(http, ct).ConfigureAwait(false);
            if (cloud.AcceptRemoteCommands) await ProcessCommandsAsync(http, cloud, ct).ConfigureAwait(false);
            counts = await _db.ReadAsync(s => SyncQueueStore.CountsAsync(s, ct), ct).ConfigureAwait(false);
            return Publish(new CloudStatus(true, true, _time.GetUtcNow(), now, null, counts.Pending, counts.Failed, counts.Conflicts));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Cloud unreachable: {Message}", e.Message);
            counts = await _db.ReadAsync(s => SyncQueueStore.CountsAsync(s, ct), ct).ConfigureAwait(false);
            return Publish(new CloudStatus(true, false, Status.LastSuccessUtc, now, e is TaskCanceledException ? "Timed out." : e.Message, counts.Pending, counts.Failed, counts.Conflicts));
        }
    }

    private async Task HeartbeatAsync(HttpClient http, CloudSettings cloud, CancellationToken ct)
    {
        var (wb, site) = await _weighing.ActiveWeighbridgeAsync(ct).ConfigureAwait(false);
        var snap = _indicator.Snapshot;
        var auto = _automation();
        var limits = await _weighing.LimitsAsync(ct).ConfigureAwait(false);
        var display = DisplayStatusEvaluator.Evaluate(snap, _time.GetUtcNow(), limits, auto?.Mode ?? OperatingMode.SemiAutomatic, false);
        var pending = await _db.ReadAsync(s => TransactionStore.PendingAsync(s, site.Id, ct), ct).ConfigureAwait(false);
        var health = await _health.CheckAsync(snap.Connection, ct).ConfigureAwait(false);
        var usable = snap.Latest is { IsUsable: true } r ? r : null;
        var request = new HeartbeatRequest(site.Code, site.Name, BackupService.AppVersion, _time.GetUtcNow(),
        [
            new WeighbridgeStatusDto(wb.Code, wb.Name, snap.Connection.ToString(), cloud.SendLiveWeight ? usable?.Weight?.ToGrams() : null, cloud.SendLiveWeight ? snap.Stability.IsStable : null,
                cloud.SendLiveWeight ? usable?.ReceivedAt : null, display.Condition.ToString(), (auto?.Mode ?? OperatingMode.SemiAutomatic).ToString(), auto?.State.ToString(), snap.LastError, pending.Count),
        ],
        new SystemHealthDto(Math.Round(health.DiskFreeGb, 1), health.LastBackupUtc, health.Sync.Pending, health.Sync.Failed, health.DatabaseProvider, health.Warnings));
        using var response = await http.PostAsJsonAsync("api/v1/sync/heartbeat", request, Api.Json, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
    }

    private async Task UploadQueueAsync(HttpClient http, CancellationToken ct)
    {
        while (true)
        {
            var now = _time.GetUtcNow();
            var due = await _db.ReadAsync(s => SyncQueueStore.DueAsync(s, now, 100, ct), ct).ConfigureAwait(false);
            if (due.Count == 0) return;
            foreach (var group in due.GroupBy(d => d.EntityType))
            {
                var path = group.Key switch
                {
                    "Transaction" => "api/v1/sync/transactions",
                    "Notification" => "api/v1/sync/notifications",
                    _ => null,
                };
                if (path is null)
                {
                    foreach (var item in group) await _db.ReadAsync(s => SyncQueueStore.MarkFailedAsync(s, item.Id, $"Unsupported entity type {item.EntityType}", now.AddDays(1), ct), ct).ConfigureAwait(false);
                    continue;
                }
                var items = group.ToList();
                var payload = "{\"items\":[" + string.Join(',', items.Select(i => i.PayloadJson)) + "]}";
                SyncBatchResult? result;
                try
                {
                    using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                    using var response = await http.PostAsync(path, content, ct).ConfigureAwait(false);
                    await EnsureAsync(response, ct).ConfigureAwait(false);
                    result = await response.Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    foreach (var item in items) await MarkFailed(item, e.Message, ct).ConfigureAwait(false);
                    throw;
                }
                var byUid = (result?.Results ?? []).ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (!byUid.TryGetValue(item.EntityUid, out var r))
                    {
                        await MarkFailed(item, "No result returned by the server.", ct).ConfigureAwait(false);
                        continue;
                    }
                    switch (r.Status)
                    {
                        case SyncItemStatus.Accepted or SyncItemStatus.Unchanged:
                            await _db.ReadAsync(s => SyncQueueStore.MarkSentAsync(s, item.Id, _time.GetUtcNow(), ct), ct).ConfigureAwait(false);
                            break;
                        case SyncItemStatus.Stale or SyncItemStatus.Conflict:
                            await _db.InTransactionAsync(async s =>
                            {
                                await SyncQueueStore.MarkConflictAsync(s, item, r.ServerVersion, r.Message ?? r.Status.ToString(), _time.GetUtcNow(), ct).ConfigureAwait(false);
                                await _audit.WriteAsync(s, UserContext.System, "Cloud", "SyncConflict", $"{item.EntityType} {item.EntityUid}: {r.Status} (local v{item.EntityVersion}, server v{r.ServerVersion})", item.EntityType, item.EntityUid, reason: r.Message, ct: ct).ConfigureAwait(false);
                                return 0;
                            }, ct).ConfigureAwait(false);
                            break;
                        default:
                            await MarkFailed(item, r.Message ?? "Rejected by the server.", ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
            if (due.Count < 100) return;
        }
    }

    private async Task UploadAuditAsync(HttpClient http, CancellationToken ct)
    {
        var cursor = await _db.ReadAsync(s => SettingsStore.GetAsync<long>(s, SettingKeys.CloudAuditCursor, ct), ct).ConfigureAwait(false);
        while (true)
        {
            var events = await _db.ReadAsync(s => AuditStore.AfterAsync(s, cursor, 500, ct), ct).ConfigureAwait(false);
            if (events.Count == 0) return;
            var batch = new SyncBatch<SyncAuditEventDto>(events.Select(e => new SyncAuditEventDto(e.Uid, e.Id, e.AtUtc, e.Username, e.Machine, e.Category, e.Action, e.EntityType, e.EntityId, e.Summary, e.Reason, e.PrevHash, e.Hash)).ToList());
            using var response = await http.PostAsJsonAsync("api/v1/sync/audit", batch, Api.Json, ct).ConfigureAwait(false);
            await EnsureAsync(response, ct).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<SyncBatchResult>(Api.Json, ct).ConfigureAwait(false);
            if (result is null || result.Results.Any(r => r.Status is SyncItemStatus.Rejected)) throw new HttpRequestException("The server rejected audit events.");
            cursor = events[^1].Id;
            var saved = cursor;
            await _db.ReadAsync(async s =>
            {
                await SettingsStore.SetAsync(s, SettingKeys.CloudAuditCursor, saved, null, _time.GetUtcNow(), ct: ct).ConfigureAwait(false);
                return 0;
            }, ct).ConfigureAwait(false);
            if (events.Count < 500) return;
        }
    }

    private async Task UploadUsersAsync(HttpClient http, CancellationToken ct)
    {
        var users = await _db.ReadAsync(s => UserStore.ListAsync(s, ct), ct).ConfigureAwait(false);
        var batch = new SyncBatch<SiteUserDto>(users.Where(u => u.Username != ServiceUsername).Select(u => new SiteUserDto(u.Username, u.DisplayName, u.RoleName, u.IsActive, u.LastLoginUtc)).ToList());
        using var response = await http.PostAsJsonAsync("api/v1/sync/users", batch, Api.Json, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        _lastUsersSent = _time.GetUtcNow();
    }

    private async Task ProcessCommandsAsync(HttpClient http, CloudSettings cloud, CancellationToken ct)
    {
        var commands = await http.GetFromJsonAsync<List<RemoteCommandDto>>("api/v1/sync/commands", Api.Json, ct).ConfigureAwait(false) ?? [];
        foreach (var c in commands)
        {
            CommandAck ack;
            try
            {
                ack = await ExecuteCommandAsync(c, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or ConcurrencyException or PermissionDeniedException or ApprovalRequiredException)
            {
                ack = new CommandAck(false, e.Message);
            }
            using var response = await http.PostAsJsonAsync($"api/v1/sync/commands/{c.Id}/ack", ack, Api.Json, ct).ConfigureAwait(false);
            await EnsureAsync(response, ct).ConfigureAwait(false);
        }
    }

    private async Task<CommandAck> ExecuteCommandAsync(RemoteCommandDto c, CancellationToken ct)
    {
        if (c.Type != CommandTypes.CancelTransaction) return new CommandAck(false, $"Command type {c.Type} is not supported by this site.");
        if (!Guid.TryParse(c.TargetUid, out var uid)) return new CommandAck(false, "Invalid transaction id.");
        var t = await _db.ReadAsync(s => TransactionStore.GetByUidAsync(s, uid, ct), ct).ConfigureAwait(false);
        if (t is null) return new CommandAck(false, "Transaction not found at this site.");
        if (t.Status == TransactionStatus.Cancelled) return new CommandAck(true, "Already cancelled.");
        var actor = await ServiceUserAsync(c.RequestedBy, ct).ConfigureAwait(false);
        await _weighing.CancelAsync(actor, t.Id, $"Cancelled remotely by {c.RequestedBy}: {c.Reason}", null, ct).ConfigureAwait(false);
        return new CommandAck(true, $"Ticket {t.TicketNo} cancelled.");
    }

    /// <summary>Local, non-interactive account that represents actions requested from the cloud (cannot sign in).</summary>
    private async Task<UserContext> ServiceUserAsync(string requestedBy, CancellationToken ct)
    {
        var id = await _db.InTransactionAsync(async s =>
        {
            var existing = await UserStore.FindByUsernameAsync(s, ServiceUsername, ct).ConfigureAwait(false);
            if (existing is not null) return existing.Id;
            var roleId = (await UserStore.RolesAsync(s, ct).ConfigureAwait(false)).Single(r => r.Name == Roles.Manager).Id;
            var now = _time.GetUtcNow();
            return await UserStore.InsertAsync(s, new UserEntity
            {
                Username = ServiceUsername, DisplayName = "Cloud (remote request)", PasswordHash = PasswordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
                RoleId = roleId, IsActive = false, CreatedUtc = now, UpdatedUtc = now,
            }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return new UserContext(id, ServiceUsername, $"Cloud: {requestedBy}", Roles.Manager, new HashSet<string> { Permissions.TransactionCancel, Permissions.ApprovalsDecide });
    }

    private async Task MarkFailed(SyncQueueEntity item, string error, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(1800, 30 * Math.Pow(2, Math.Min(item.Attempts, 10))));
        await _db.ReadAsync(s => SyncQueueStore.MarkFailedAsync(s, item.Id, error, _time.GetUtcNow() + delay, ct), ct).ConfigureAwait(false);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)) message = e.GetString();
        }
        catch (JsonException) { }
        message ??= body.Length > 200 ? body[..200] : body;
        throw new HttpRequestException($"Cloud returned {(int)response.StatusCode} {response.ReasonPhrase}: {message}", null, response.StatusCode);
    }

    private CloudStatus Publish(CloudStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _running.Dispose();
    }
}
