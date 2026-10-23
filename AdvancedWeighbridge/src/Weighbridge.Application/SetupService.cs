using Weighbridge.Application.Settings;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application;

public sealed record SetupRequest
{
    public required CompanySettings Company { get; init; }
    public required string AdminUsername { get; init; }
    public required string AdminDisplayName { get; init; }
    public required string AdminPassword { get; init; }
    public string SiteCode { get; init; } = "SITE1";
    public string SiteName { get; init; } = "Main site";
    public string WeighbridgeCode { get; init; } = "WB1";
    public string WeighbridgeName { get; init; } = "Weighbridge 1";
    public decimal CapacityKg { get; init; } = 80_000m;
    public decimal MinimumCaptureKg { get; init; } = 200m;
    public decimal DivisionKg { get; init; } = 10m;
    public string Language { get; init; } = "en";
    public IndicatorProfile? Profile { get; init; }
}

/// <summary>First-run initialisation: roles, administrator, site, weighbridge, defaults.</summary>
public sealed class SetupService(Database db, SettingsService settings, AuditService audit, TimeProvider time)
{
    public static readonly string[] DefaultVehicleCategories = ["Lorry", "Tipper", "Prime mover / trailer", "Container truck", "Tractor", "Van / pickup", "Bowser"];

    public Task<bool> IsInitializedAsync(CancellationToken ct = default) =>
        db.ReadAsync(async s => await UserStore.CountAsync(s, ct).ConfigureAwait(false) > 0, ct);

    public async Task<long> InitializeAsync(SetupRequest request, CancellationToken ct = default)
    {
        if (await IsInitializedAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("The system is already initialised.");
        var problems = new PasswordPolicy().Check(request.AdminPassword, request.AdminUsername);
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        if (request.CapacityKg <= 0 || request.MinimumCaptureKg < 0 || request.MinimumCaptureKg >= request.CapacityKg) throw new ArgumentException("Capacity and minimum capture weight are not valid.");

        var now = time.GetUtcNow();
        var profile = request.Profile ?? DefaultProfiles.Simulator();
        var (adminId, weighbridgeId) = await db.InTransactionAsync(async s =>
        {
            long adminRole = 0;
            foreach (var (role, permissions) in Roles.Defaults)
            {
                var id = await UserStore.EnsureRoleAsync(s, role, null, permissions, ct).ConfigureAwait(false);
                if (role == Roles.Administrator) adminRole = id;
            }
            var adminId = await UserStore.InsertAsync(s, new UserEntity
            {
                Username = request.AdminUsername.Trim(), DisplayName = request.AdminDisplayName.Trim(), PasswordHash = PasswordHasher.Hash(request.AdminPassword),
                RoleId = adminRole, PasswordChangedUtc = now, CreatedUtc = now, UpdatedUtc = now,
            }, ct).ConfigureAwait(false);
            var siteId = await SiteStore.InsertSiteAsync(s, new SiteEntity { Code = request.SiteCode.Trim().ToUpperInvariant(), Name = request.SiteName.Trim(), Address = request.Company.Address }, ct).ConfigureAwait(false);
            var wbId = await SiteStore.InsertWeighbridgeAsync(s, new WeighbridgeEntity
            {
                SiteId = siteId, Code = request.WeighbridgeCode.Trim().ToUpperInvariant(), Name = request.WeighbridgeName.Trim(),
                CapacityG = (long)(request.CapacityKg * 1000), MinCaptureG = (long)(request.MinimumCaptureKg * 1000), DivisionG = (long)(request.DivisionKg * 1000),
            }, ct).ConfigureAwait(false);
            var profileId = await SiteStore.SaveProfileAsync(s, profile, adminId, now, ct).ConfigureAwait(false);
            await SiteStore.AssignProfileAsync(s, wbId, profileId, null, now, ct).ConfigureAwait(false);
            foreach (var c in DefaultVehicleCategories) await MasterDataStore.InsertCategoryAsync(s, new VehicleCategoryEntity { Name = c }, ct).ConfigureAwait(false);
            await TicketTemplateStore.SaveAsync(s, new TicketTemplateEntity { Name = "Standard A5", Paper = "A5", TemplateJson = "{}", IsDefault = true, UpdatedUtc = now }, ct).ConfigureAwait(false);
            await SettingsStore.SetAsync(s, SettingKeys.InstallationId, Guid.NewGuid().ToString("D"), adminId, now, ct: ct).ConfigureAwait(false);
            return (adminId, wbId);
        }, ct).ConfigureAwait(false);

        var admin = new UserContext(adminId, request.AdminUsername, request.AdminDisplayName, Roles.Administrator, Permissions.All.ToHashSet());
        await settings.SaveInternalAsync(SettingKeys.Company, request.Company, admin, ct: ct).ConfigureAwait(false);
        await settings.SaveInternalAsync(SettingKeys.Localization, new LocalizationSettings { Language = request.Language }, admin, ct: ct).ConfigureAwait(false);
        await settings.SaveInternalAsync(SettingKeys.Weighing, new WeighingSettings { ActiveWeighbridgeId = weighbridgeId }, admin, ct: ct).ConfigureAwait(false);
        await settings.SaveInternalAsync(SettingKeys.Printing, new PrintingSettings { Template = new Reporting.Tickets.TicketTemplate { Language = request.Language } }, admin, ct: ct).ConfigureAwait(false);
        await audit.WriteAsync(admin, "System", "Initialised", $"System initialised for {request.Company.Name} (site {request.SiteCode}, weighbridge {request.WeighbridgeCode})", ct: ct).ConfigureAwait(false);
        return adminId;
    }
}

/// <summary>Ready-made indicator profiles (examples; always verify against the indicator manual and a reference load).</summary>
public static class DefaultProfiles
{
    public static IndicatorProfile Simulator() => new()
    {
        Id = Guid.Parse("8f6d4c55-38c3-4f4f-9b9d-0d8a5c1c0001"),
        Name = "Built-in simulator",
        Manufacturer = "AWMS",
        Model = "Virtual indicator",
        Notes = "Simulated weighbridge for training, testing and demonstrations. Output: ST/US,GS,+000000.0kg CR LF.",
        Connection = new ConnectionSettings { Transport = TransportKind.Simulator, Simulator = new SimulatorSettings { AutoCycle = false } },
        Verified = true,
    };

    public static IndicatorProfile GenericAscii(string port) => new()
    {
        Name = "Generic ASCII (ST/US,GS,weight,kg)",
        Manufacturer = "Generic",
        Connection = new ConnectionSettings { Serial = new SerialSettings { PortName = port } },
    };
}
