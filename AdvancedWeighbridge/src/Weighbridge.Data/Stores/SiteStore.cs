using Weighbridge.Core.Indicators;

namespace Weighbridge.Data.Stores;

/// <summary>Sites, weighbridges, indicator profiles (versioned) and their assignment to weighbridges.</summary>
public static class SiteStore
{
    public static async Task<IReadOnlyList<SiteEntity>> SitesAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<SiteEntity>("SELECT id, uid, code, name, address, phone, is_active FROM sites ORDER BY code", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<SiteEntity?> GetSiteAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<SiteEntity>("SELECT id, uid, code, name, address, phone, is_active FROM sites WHERE id = @id", new { id }, ct);

    public static Task<long> InsertSiteAsync(DbSession s, SiteEntity e, CancellationToken ct = default) =>
        s.ExecuteScalarAsync<long>("INSERT INTO sites (uid, code, name, address, phone, is_active) VALUES (@Uid, @Code, @Name, @Address, @Phone, @IsActive) RETURNING id", e, ct)!;

    public static Task<int> UpdateSiteAsync(DbSession s, SiteEntity e, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE sites SET code = @Code, name = @Name, address = @Address, phone = @Phone, is_active = @IsActive WHERE id = @Id", e, ct);

    public static async Task<IReadOnlyList<WeighbridgeEntity>> WeighbridgesAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<WeighbridgeEntity>("SELECT id, uid, site_id, code, name, capacity_g, min_capture_g, division_g, is_active FROM weighbridges ORDER BY site_id, code", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<WeighbridgeEntity?> GetWeighbridgeAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<WeighbridgeEntity>("SELECT id, uid, site_id, code, name, capacity_g, min_capture_g, division_g, is_active FROM weighbridges WHERE id = @id", new { id }, ct);

    public static Task<long> InsertWeighbridgeAsync(DbSession s, WeighbridgeEntity e, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO weighbridges (uid, site_id, code, name, capacity_g, min_capture_g, division_g, is_active)
        VALUES (@Uid, @SiteId, @Code, @Name, @CapacityG, @MinCaptureG, @DivisionG, @IsActive) RETURNING id
        """, e, ct)!;

    public static Task<int> UpdateWeighbridgeAsync(DbSession s, WeighbridgeEntity e, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE weighbridges SET code = @Code, name = @Name, capacity_g = @CapacityG, min_capture_g = @MinCaptureG, division_g = @DivisionG, is_active = @IsActive
        WHERE id = @Id
        """, e, ct);

    // ---------------------------------------------------------------- profiles

    private const string ProfileColumns = "id, uid, name, manufacturer, model, version, profile_json, verified, is_active, created_utc, updated_utc";

    public static async Task<IReadOnlyList<ProfileEntity>> ProfilesAsync(DbSession s, bool includeInactive = false, CancellationToken ct = default) =>
        (await s.QueryAsync<ProfileEntity>($"SELECT {ProfileColumns} FROM indicator_profiles {(includeInactive ? "" : "WHERE is_active = @active")} ORDER BY name",
            new { active = true }, ct).ConfigureAwait(false)).ToList();

    public static Task<ProfileEntity?> GetProfileAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<ProfileEntity>($"SELECT {ProfileColumns} FROM indicator_profiles WHERE id = @id", new { id }, ct);

    public static Task<ProfileEntity?> GetProfileByUidAsync(DbSession s, Guid uid, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<ProfileEntity>($"SELECT {ProfileColumns} FROM indicator_profiles WHERE uid = @uid", new { uid }, ct);

    /// <summary>Inserts or updates a profile and records the version in the immutable history.</summary>
    public static async Task<long> SaveProfileAsync(DbSession s, IndicatorProfile profile, long? userId, DateTimeOffset at, CancellationToken ct = default)
    {
        var json = profile.ToJson();
        var existing = await GetProfileByUidAsync(s, profile.Id, ct).ConfigureAwait(false);
        long id;
        if (existing is null)
        {
            id = await s.ExecuteScalarAsync<long>($"""
                INSERT INTO indicator_profiles (uid, name, manufacturer, model, version, profile_json, verified, is_active, created_utc, updated_utc)
                VALUES (@uid, @name, @manufacturer, @model, @version, @json, @verified, @active, @at, @at) RETURNING id
                """, new { uid = profile.Id, name = profile.Name, manufacturer = profile.Manufacturer, model = profile.Model, version = profile.Version, json, verified = profile.Verified, active = true, at }, ct).ConfigureAwait(false);
        }
        else
        {
            if (profile.Version <= existing.Version) throw new ConcurrencyException($"Profile '{profile.Name}' version {profile.Version} is not newer than the stored version {existing.Version}.");
            id = existing.Id;
            await s.ExecuteAsync("""
                UPDATE indicator_profiles SET name = @name, manufacturer = @manufacturer, model = @model, version = @version, profile_json = @json,
                    verified = @verified, updated_utc = @at WHERE id = @id
                """, new { id, name = profile.Name, manufacturer = profile.Manufacturer, model = profile.Model, version = profile.Version, json, verified = profile.Verified, at }, ct).ConfigureAwait(false);
        }
        await s.ExecuteAsync("INSERT INTO indicator_profile_versions (profile_id, version, profile_json, changed_by, changed_utc) VALUES (@id, @version, @json, @userId, @at)",
            new { id, version = profile.Version, json, userId, at }, ct).ConfigureAwait(false);
        return id;
    }

    public static Task<int> SetProfileActiveAsync(DbSession s, long id, bool active, DateTimeOffset at, CancellationToken ct = default) =>
        s.ExecuteAsync("UPDATE indicator_profiles SET is_active = @active, updated_utc = @at WHERE id = @id", new { id, active, at }, ct);

    public static Task<string?> ProfileVersionJsonAsync(DbSession s, Guid uid, int version, CancellationToken ct = default) => s.QuerySingleOrDefaultAsync<string>("""
        SELECT v.profile_json FROM indicator_profile_versions v JOIN indicator_profiles p ON p.id = v.profile_id WHERE p.uid = @uid AND v.version = @version
        """, new { uid, version }, ct);

    // ---------------------------------------------------------------- assignments

    public static Task<IndicatorDeviceEntity?> DeviceForWeighbridgeAsync(DbSession s, long weighbridgeId, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<IndicatorDeviceEntity>("""
            SELECT id, weighbridge_id, profile_id, port_override, is_enabled, updated_utc FROM indicator_devices
            WHERE weighbridge_id = @weighbridgeId AND is_enabled = @enabled ORDER BY id DESC LIMIT 1
            """, new { weighbridgeId, enabled = true }, ct);

    public static async Task AssignProfileAsync(DbSession s, long weighbridgeId, long profileId, string? portOverride, DateTimeOffset at, CancellationToken ct = default)
    {
        await s.ExecuteAsync("UPDATE indicator_devices SET is_enabled = @disabled, updated_utc = @at WHERE weighbridge_id = @weighbridgeId", new { weighbridgeId, disabled = false, at }, ct).ConfigureAwait(false);
        await s.ExecuteAsync("INSERT INTO indicator_devices (weighbridge_id, profile_id, port_override, is_enabled, updated_utc) VALUES (@weighbridgeId, @profileId, @portOverride, @enabled, @at)",
            new { weighbridgeId, profileId, portOverride, enabled = true, at }, ct).ConfigureAwait(false);
    }
}
