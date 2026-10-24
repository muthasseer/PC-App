using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Weighbridge.Application.Security;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Settings;

/// <summary>
/// Typed settings with an in-memory cache. Every change is audited with the old and new value (secrets redacted).
/// </summary>
public sealed class SettingsService(Database db, ISecretProtector protector, AuditService audit, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, object> _cache = new(StringComparer.Ordinal);

    public event EventHandler<string>? Changed;

    public ISecretProtector Protector => protector;

    public async Task<T> GetAsync<T>(string key, CancellationToken ct = default) where T : new()
    {
        if (_cache.TryGetValue(key, out var cached)) return (T)cached;
        var value = await db.ReadAsync(s => SettingsStore.GetAsync<T>(s, key, ct), ct).ConfigureAwait(false) ?? new T();
        _cache[key] = value!;
        return value;
    }

    public async Task SaveAsync<T>(string key, T value, UserContext user, string? reason = null, CancellationToken ct = default) where T : new()
    {
        user.Demand(Permissions.SettingsManage);
        await SaveInternalAsync(key, value, user, reason, ct).ConfigureAwait(false);
    }

    /// <summary>Used by setup and by services that keep their own state (cursors), without a permission check.</summary>
    internal async Task SaveInternalAsync<T>(string key, T value, UserContext user, string? reason = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var newJson = JsonSerializer.Serialize(value, SettingsStore.Json);
        await db.InTransactionAsync(async s =>
        {
            var oldJson = await SettingsStore.GetRawAsync(s, key, ct).ConfigureAwait(false);
            await SettingsStore.SetRawAsync(s, key, newJson, user.UserId == 0 ? null : user.UserId, now, ct: ct).ConfigureAwait(false);
            if (oldJson != newJson && !key.StartsWith("cloud.", StringComparison.Ordinal))
            {
                await audit.WriteAsync(s, user, "Settings", "Changed", $"Settings '{key}' changed", "Setting", key, Redact(oldJson), Redact(newJson), reason, ct).ConfigureAwait(false);
            }
            return 0;
        }, ct).ConfigureAwait(false);
        _cache[key] = value!;
        Changed?.Invoke(this, key);
    }

    public void Invalidate() => _cache.Clear();

    /// <summary>Per-installation secret used to sign ticket QR codes (created on first use).</summary>
    public async Task<byte[]> TicketSigningKeyAsync(CancellationToken ct = default)
    {
        var stored = await db.ReadAsync(s => SettingsStore.GetAsync<string>(s, SettingKeys.TicketSigningKey, ct), ct).ConfigureAwait(false);
        if (stored is not null) return Convert.FromBase64String(protector.Unprotect(stored));
        var key = RandomNumberGenerator.GetBytes(32);
        await db.InTransactionAsync(async s =>
        {
            await SettingsStore.SetAsync(s, SettingKeys.TicketSigningKey, protector.Protect(Convert.ToBase64String(key)), null, time.GetUtcNow(), isSecret: true, ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
        return key;
    }

    private static string? Redact(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
            var dict = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Name.Contains("protected", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    ? JsonSerializer.SerializeToElement("***")
                    : p.Value;
            }
            return JsonSerializer.Serialize(dict);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
