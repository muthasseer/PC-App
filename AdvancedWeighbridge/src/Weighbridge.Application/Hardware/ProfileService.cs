using Weighbridge.Application.Settings;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Hardware;

public sealed record ProfileSummary(long Id, Guid Uid, string Name, string? Manufacturer, string? Model, int Version, bool Verified, bool IsActive, bool InUse, DateTimeOffset UpdatedUtc);

/// <summary>
/// Indicator profiles: list, import/export, save (every change is a new, retained version) and activation on the
/// active weighbridge. Activating a profile restarts the indicator connection with it.
/// </summary>
public sealed class ProfileService(Database db, SettingsService settings, AuditService audit, IndicatorManager indicator, ProtocolRegistry protocols, TimeProvider time)
{
    public async Task<IReadOnlyList<ProfileSummary>> ProfilesAsync(CancellationToken ct = default)
    {
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        return await db.ReadAsync(async s =>
        {
            var device = await SiteStore.DeviceForWeighbridgeAsync(s, weighing.ActiveWeighbridgeId, ct).ConfigureAwait(false);
            return (await SiteStore.ProfilesAsync(s, includeInactive: true, ct).ConfigureAwait(false))
                .Select(p => new ProfileSummary(p.Id, p.Uid, p.Name, p.Manufacturer, p.Model, p.Version, p.Verified, p.IsActive, p.Id == device?.ProfileId, p.UpdatedUtc))
                .ToList();
        }, ct).ConfigureAwait(false);
    }

    public Task<IndicatorProfile?> LoadAsync(long id, CancellationToken ct = default) =>
        db.ReadAsync(async s => (await SiteStore.GetProfileAsync(s, id, ct).ConfigureAwait(false))?.ToProfile(), ct);

    /// <summary>The profile in use on the active weighbridge (with the port override applied), if any.</summary>
    public async Task<(IndicatorProfile? Profile, string? PortOverride)> ActiveAsync(CancellationToken ct = default)
    {
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        return await db.ReadAsync(async s =>
        {
            var device = await SiteStore.DeviceForWeighbridgeAsync(s, weighing.ActiveWeighbridgeId, ct).ConfigureAwait(false);
            if (device is null) return ((IndicatorProfile?)null, (string?)null);
            return ((await SiteStore.GetProfileAsync(s, device.ProfileId, ct).ConfigureAwait(false))?.ToProfile(), device.PortOverride);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the profile (as the next version when it already exists) and, when requested, assigns it to the active
    /// weighbridge and reconnects the indicator. Invalid profiles are refused.
    /// </summary>
    public async Task<IndicatorProfile> SaveAsync(UserContext user, IndicatorProfile profile, bool activate, string? reason = null, CancellationToken ct = default)
    {
        user.Demand(Permissions.DevicesConfigure);
        var problems = profile.Validate(protocols);
        if (problems.Count > 0) throw new ArgumentException("The profile is not valid: " + string.Join(" ", problems));
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var saved = await db.InTransactionAsync(async s =>
        {
            var existing = await SiteStore.GetProfileByUidAsync(s, profile.Id, ct).ConfigureAwait(false);
            var toSave = existing is null ? profile : profile with { Version = Math.Max(profile.Version, existing.Version + 1) };
            var id = await SiteStore.SaveProfileAsync(s, toSave, user.UserId == 0 ? null : user.UserId, now, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Devices", existing is null ? "ProfileCreated" : "ProfileChanged",
                $"Indicator profile '{toSave.Name}' saved as version {toSave.Version}{(toSave.Verified ? " (verified against a reference load)" : " (not verified)")}",
                "IndicatorProfile", toSave.Id.ToString("D"), existing is null ? null : existing.ProfileJson, toSave.ToJson(), reason, ct).ConfigureAwait(false);
            if (activate)
            {
                if (weighing.ActiveWeighbridgeId == 0) throw new InvalidOperationException("No active weighbridge is configured.");
                await SiteStore.AssignProfileAsync(s, weighing.ActiveWeighbridgeId, id, null, now, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "Devices", "ProfileActivated", $"Indicator profile '{toSave.Name}' v{toSave.Version} activated on the weighbridge ({Describe(toSave)})",
                    "IndicatorProfile", toSave.Id.ToString("D"), reason: reason, ct: ct).ConfigureAwait(false);
            }
            return toSave;
        }, ct).ConfigureAwait(false);
        if (activate) await indicator.StartAsync(ct).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Reads a profile file. The imported profile gets a new identity unless it is a newer version of a stored one.</summary>
    public async Task<IndicatorProfile> ImportAsync(UserContext user, string json, CancellationToken ct = default)
    {
        user.Demand(Permissions.DevicesConfigure);
        var profile = IndicatorProfile.FromJson(json);
        var problems = profile.Validate(protocols);
        if (problems.Count > 0) throw new ArgumentException("The profile file is not valid: " + string.Join(" ", problems));
        var known = await db.ReadAsync(s => SiteStore.GetProfileByUidAsync(s, profile.Id, ct), ct).ConfigureAwait(false);
        // An imported profile was not verified on this weighbridge.
        profile = profile with { Verified = false, VerifiedAt = null, VerifiedBy = null };
        if (known is not null && profile.Version <= known.Version) profile = profile with { Id = Guid.NewGuid(), Version = 1, Name = profile.Name + " (imported)" };
        return await SaveAsync(user, profile, activate: false, "Imported from file", ct).ConfigureAwait(false);
    }

    public async Task SetActiveFlagAsync(UserContext user, long id, bool active, CancellationToken ct = default)
    {
        user.Demand(Permissions.DevicesConfigure);
        var now = time.GetUtcNow();
        await db.InTransactionAsync(async s =>
        {
            var p = await SiteStore.GetProfileAsync(s, id, ct).ConfigureAwait(false) ?? throw new ArgumentException("Profile not found.");
            await SiteStore.SetProfileActiveAsync(s, id, active, now, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Devices", active ? "ProfileEnabled" : "ProfileArchived", $"Indicator profile '{p.Name}' {(active ? "enabled" : "archived")}",
                "IndicatorProfile", p.Uid.ToString("D"), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Capacity, minimum capture weight and division of the active weighbridge.</summary>
    public async Task UpdateWeighbridgeAsync(UserContext user, string name, decimal capacityKg, decimal minimumKg, decimal divisionKg, CancellationToken ct = default)
    {
        user.Demand(Permissions.DevicesConfigure);
        if (capacityKg <= 0 || minimumKg < 0 || minimumKg >= capacityKg || divisionKg <= 0) throw new ArgumentException("Capacity, minimum capture weight and division are not valid.");
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        await db.InTransactionAsync(async s =>
        {
            var wb = await SiteStore.GetWeighbridgeAsync(s, weighing.ActiveWeighbridgeId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("No active weighbridge.");
            var before = $"{wb.Name}: capacity {wb.CapacityG / 1000m:0} kg, minimum {wb.MinCaptureG / 1000m:0} kg, division {wb.DivisionG / 1000m:0.###} kg";
            wb.Name = name.Trim().Length == 0 ? wb.Name : name.Trim();
            wb.CapacityG = (long)(capacityKg * 1000);
            wb.MinCaptureG = (long)(minimumKg * 1000);
            wb.DivisionG = (long)(divisionKg * 1000);
            await SiteStore.UpdateWeighbridgeAsync(s, wb, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Devices", "WeighbridgeChanged",
                $"Weighbridge {wb.Code} changed: {wb.Name}: capacity {capacityKg:0} kg, minimum {minimumKg:0} kg, division {divisionKg:0.###} kg", "Weighbridge", wb.Code,
                before, null, ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    private static string Describe(IndicatorProfile p) => p.Connection.Transport switch
    {
        TransportKind.Serial => p.Connection.Serial.Summary,
        TransportKind.TcpClient => $"TCP {p.Connection.Tcp.Host}:{p.Connection.Tcp.Port}",
        TransportKind.Replay => "replay " + Path.GetFileName(p.Connection.ReplayFile),
        _ => "simulator",
    };
}
