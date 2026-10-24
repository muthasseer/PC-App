using System.Globalization;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application;

/// <summary>Vehicles, customers, suppliers, materials, drivers, transport companies and orders – with permissions and audit.</summary>
public sealed class MasterDataService(Database db, AuditService audit, TimeProvider time)
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- vehicles

    public Task<PagedResult<VehicleEntity>> SearchVehiclesAsync(UserContext user, string? text, int page = 1, int pageSize = 50, bool includeInactive = false, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataView);
        return db.ReadAsync(s => MasterDataStore.SearchVehiclesAsync(s, text, page, pageSize, includeInactive, ct), ct);
    }

    public Task<VehicleEntity?> VehicleAsync(string registration, CancellationToken ct = default) => db.ReadAsync(s => MasterDataStore.VehicleByRegistrationAsync(s, registration, ct), ct);

    /// <summary>RFID tag or QR code → registration (used by identification sources).</summary>
    public string? ResolveTag(string code) => db.ReadAsync(async s => (await MasterDataStore.VehicleByTagAsync(s, code).ConfigureAwait(false))?.Registration).GetAwaiter().GetResult();

    public async Task<long> SaveVehicleAsync(UserContext user, VehicleEntity v, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (string.IsNullOrWhiteSpace(v.Registration) || VehicleRegistration.Normalize(v.Registration).Length < 2) throw new ArgumentException("A vehicle registration number is required.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            v.UpdatedUtc = now;
            if (v.Id == 0)
            {
                if (v.IsBlacklisted) user.Demand(Permissions.VehicleBlacklist);
                v.CreatedUtc = now;
                v.Id = await MasterDataStore.InsertVehicleAsync(s, v, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "MasterData", "VehicleCreated", $"Vehicle {v.Registration} created", "Vehicle", Id(v.Id), newJson: AuditService.ToJson(v), ct: ct).ConfigureAwait(false);
            }
            else
            {
                var before = await MasterDataStore.VehicleAsync(s, v.Id, ct).ConfigureAwait(false) ?? throw new ArgumentException("Vehicle not found.");
                if (before.IsBlacklisted != v.IsBlacklisted) user.Demand(Permissions.VehicleBlacklist);
                await MasterDataStore.UpdateVehicleAsync(s, v, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "MasterData", "VehicleUpdated", $"Vehicle {v.Registration} updated", "Vehicle", Id(v.Id), AuditService.ToJson(before), AuditService.ToJson(v), ct: ct).ConfigureAwait(false);
            }
            return v.Id;
        }, ct).ConfigureAwait(false);
    }

    public async Task SetBlacklistAsync(UserContext user, long vehicleId, bool blacklisted, string? reason, CancellationToken ct = default)
    {
        user.Demand(Permissions.VehicleBlacklist);
        if (blacklisted && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to blacklist a vehicle.");
        await db.InTransactionAsync(async s =>
        {
            var v = await MasterDataStore.VehicleAsync(s, vehicleId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Vehicle not found.");
            v.IsBlacklisted = blacklisted;
            v.BlacklistReason = blacklisted ? reason : null;
            v.UpdatedUtc = time.GetUtcNow();
            await MasterDataStore.UpdateVehicleAsync(s, v, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "MasterData", blacklisted ? "VehicleBlacklisted" : "VehicleUnblacklisted", $"Vehicle {v.Registration} {(blacklisted ? "blacklisted" : "removed from blacklist")}", "Vehicle", Id(vehicleId), reason: reason, ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Records a stored tare. It can only be used for weighing once approved (immediately, if the user may approve tares).</summary>
    public async Task SetStoredTareAsync(UserContext user, long vehicleId, decimal tareKg, string source, long? transactionId, int validityDays, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (tareKg <= 0) throw new ArgumentException("The tare must be positive.");
        var now = time.GetUtcNow();
        var approver = user.Has(Permissions.VehicleTareApprove) ? user.UserId : (long?)null;
        await db.InTransactionAsync(async s =>
        {
            await MasterDataStore.SetStoredTareAsync(s, vehicleId, (long)Math.Round(tareKg * 1000m), source, transactionId, user.UserId, approver, now, now.AddDays(validityDays), ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "MasterData", "StoredTareSet", $"Stored tare {tareKg:0.###} kg recorded ({source}){(approver is null ? ", awaiting approval" : ", approved")}", "Vehicle", Id(vehicleId), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task ApproveStoredTareAsync(UserContext user, long vehicleId, int validityDays, CancellationToken ct = default)
    {
        user.Demand(Permissions.VehicleTareApprove);
        var now = time.GetUtcNow();
        await db.InTransactionAsync(async s =>
        {
            if (await MasterDataStore.ApproveStoredTareAsync(s, vehicleId, user.UserId, now, now.AddDays(validityDays), ct).ConfigureAwait(false) != 1) throw new InvalidOperationException("The vehicle has no stored tare to approve.");
            await audit.WriteAsync(s, user, "MasterData", "StoredTareApproved", "Stored tare approved", "Vehicle", Id(vehicleId), ct: ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- parties and materials

    public Task<PagedResult<PartyEntity>> SearchPartiesAsync(UserContext user, PartyKind kind, string? text, int page = 1, int pageSize = 50, bool includeInactive = false, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataView);
        return db.ReadAsync(s => MasterDataStore.SearchPartiesAsync(s, kind, text, page, pageSize, includeInactive, ct), ct);
    }

    public async Task<long> SavePartyAsync(UserContext user, PartyKind kind, PartyEntity e, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("A name is required.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            e.UpdatedUtc = now;
            if (e.Id == 0)
            {
                e.CreatedUtc = now;
                e.Id = await MasterDataStore.InsertPartyAsync(s, kind, e, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "MasterData", $"{kind}Created", $"{kind} {e.Name} created", kind.ToString(), Id(e.Id), newJson: AuditService.ToJson(e), ct: ct).ConfigureAwait(false);
            }
            else
            {
                var before = await MasterDataStore.PartyAsync(s, kind, e.Id, ct).ConfigureAwait(false);
                await MasterDataStore.UpdatePartyAsync(s, kind, e, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "MasterData", $"{kind}Updated", $"{kind} {e.Name} updated", kind.ToString(), Id(e.Id), AuditService.ToJson(before), AuditService.ToJson(e), ct: ct).ConfigureAwait(false);
            }
            return e.Id;
        }, ct).ConfigureAwait(false);
    }

    public Task<PagedResult<MaterialEntity>> SearchMaterialsAsync(UserContext user, string? text, int page = 1, int pageSize = 50, bool includeInactive = false, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataView);
        return db.ReadAsync(s => MasterDataStore.SearchMaterialsAsync(s, text, page, pageSize, includeInactive, ct), ct);
    }

    public async Task<long> SaveMaterialAsync(UserContext user, MaterialEntity e, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("A name is required.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            e.UpdatedUtc = now;
            if (e.Id == 0)
            {
                e.CreatedUtc = now;
                e.Id = await MasterDataStore.InsertMaterialAsync(s, e, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "MasterData", "MaterialCreated", $"Material {e.Name} created", "Material", Id(e.Id), newJson: AuditService.ToJson(e), ct: ct).ConfigureAwait(false);
            }
            else
            {
                var before = await MasterDataStore.MaterialAsync(s, e.Id, ct).ConfigureAwait(false);
                await MasterDataStore.UpdateMaterialAsync(s, e, ct).ConfigureAwait(false);
                await audit.WriteAsync(s, user, "MasterData", "MaterialUpdated", $"Material {e.Name} updated", "Material", Id(e.Id), AuditService.ToJson(before), AuditService.ToJson(e), ct: ct).ConfigureAwait(false);
            }
            return e.Id;
        }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- drivers, transporters, categories, orders

    public Task<IReadOnlyList<DriverEntity>> DriversAsync(string? text = null, CancellationToken ct = default) => db.ReadAsync(s => MasterDataStore.DriversAsync(s, text, ct), ct);

    public async Task<long> SaveDriverAsync(UserContext user, DriverEntity e, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("A name is required.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            e.UpdatedUtc = now;
            if (e.Id == 0)
            {
                e.CreatedUtc = now;
                e.Id = await MasterDataStore.InsertDriverAsync(s, e, ct).ConfigureAwait(false);
            }
            else
            {
                await MasterDataStore.UpdateDriverAsync(s, e, ct).ConfigureAwait(false);
            }
            await audit.WriteAsync(s, user, "MasterData", "DriverSaved", $"Driver {e.Name} saved", "Driver", Id(e.Id), ct: ct).ConfigureAwait(false);
            return e.Id;
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TransportCompanyEntity>> TransportCompaniesAsync(CancellationToken ct = default) => db.ReadAsync(s => MasterDataStore.TransportCompaniesAsync(s, ct), ct);

    public async Task<long> SaveTransportCompanyAsync(UserContext user, TransportCompanyEntity e, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("A name is required.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            e.UpdatedUtc = now;
            if (e.Id == 0)
            {
                e.CreatedUtc = now;
                e.Id = await MasterDataStore.InsertTransportCompanyAsync(s, e, ct).ConfigureAwait(false);
            }
            else
            {
                await MasterDataStore.UpdateTransportCompanyAsync(s, e, ct).ConfigureAwait(false);
            }
            await audit.WriteAsync(s, user, "MasterData", "TransporterSaved", $"Transport company {e.Name} saved", "TransportCompany", Id(e.Id), ct: ct).ConfigureAwait(false);
            return e.Id;
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VehicleCategoryEntity>> CategoriesAsync(CancellationToken ct = default) => db.ReadAsync(s => MasterDataStore.CategoriesAsync(s, ct), ct);

    public Task<IReadOnlyList<OrderEntity>> OpenOrdersAsync(CancellationToken ct = default) => db.ReadAsync(s => MasterDataStore.OpenOrdersAsync(s, ct), ct);

    public async Task<long> SaveOrderAsync(UserContext user, OrderEntity e, CancellationToken ct = default)
    {
        user.Demand(Permissions.MasterDataEdit);
        if (string.IsNullOrWhiteSpace(e.OrderNo)) throw new ArgumentException("An order number is required.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            e.UpdatedUtc = now;
            if (e.Id == 0)
            {
                e.CreatedUtc = now;
                e.Id = await MasterDataStore.InsertOrderAsync(s, e, ct).ConfigureAwait(false);
            }
            else
            {
                await MasterDataStore.UpdateOrderAsync(s, e, ct).ConfigureAwait(false);
            }
            await audit.WriteAsync(s, user, "MasterData", "OrderSaved", $"{e.OrderType} order {e.OrderNo} saved", "Order", Id(e.Id), newJson: AuditService.ToJson(e), ct: ct).ConfigureAwait(false);
            return e.Id;
        }, ct).ConfigureAwait(false);
    }
}
