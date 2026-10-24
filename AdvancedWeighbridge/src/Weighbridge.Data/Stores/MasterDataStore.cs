using Weighbridge.Core.Weighing;

namespace Weighbridge.Data.Stores;

/// <summary>Vehicles, customers, suppliers, materials, drivers, transport companies, categories and orders.</summary>
public static class MasterDataStore
{
    // ---------------------------------------------------------------- vehicles

    private const string VehicleColumns = """
        v.id, v.uid, v.registration, v.registration_norm, v.category_id, c.name AS category_name, v.vehicle_type, v.make_model, v.axles,
        v.transport_company_id, v.default_driver_id, v.rfid_tag, v.qr_code, v.stored_tare_g, v.stored_tare_utc, v.stored_tare_approved_by,
        v.stored_tare_approved_utc, v.stored_tare_expires_utc, v.is_blacklisted, v.blacklist_reason, v.photo_path, v.notes, v.is_active,
        v.created_utc, v.updated_utc
        """;

    public static Task<VehicleEntity?> VehicleByRegistrationAsync(DbSession s, string registration, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<VehicleEntity>($"SELECT {VehicleColumns} FROM vehicles v LEFT JOIN vehicle_categories c ON c.id = v.category_id WHERE v.registration_norm = @norm",
            new { norm = VehicleRegistration.Normalize(registration) }, ct);

    public static Task<VehicleEntity?> VehicleAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<VehicleEntity>($"SELECT {VehicleColumns} FROM vehicles v LEFT JOIN vehicle_categories c ON c.id = v.category_id WHERE v.id = @id", new { id }, ct);

    public static Task<VehicleEntity?> VehicleByTagAsync(DbSession s, string code, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<VehicleEntity>($"SELECT {VehicleColumns} FROM vehicles v LEFT JOIN vehicle_categories c ON c.id = v.category_id WHERE v.rfid_tag = @code OR v.qr_code = @code",
            new { code }, ct);

    public static async Task<PagedResult<VehicleEntity>> SearchVehiclesAsync(DbSession s, string? text, int page = 1, int pageSize = 50, bool includeInactive = false, CancellationToken ct = default)
    {
        var where = new List<string>();
        if (!includeInactive) where.Add("v.is_active = @active");
        if (!string.IsNullOrWhiteSpace(text))
        {
            where.Add($"({s.Dialect.ContainsCi("v.registration_norm", "@norm")} OR {s.Dialect.ContainsCi("v.registration", "@like")} OR {s.Dialect.ContainsCi("COALESCE(v.make_model, '')", "@like")} OR {s.Dialect.ContainsCi("COALESCE(v.rfid_tag, '')", "@like")})");
        }
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        var p = new { active = true, norm = SqlDialect.LikePattern(VehicleRegistration.Normalize(text)), like = SqlDialect.LikePattern(text ?? string.Empty), limit = pageSize, offset = (Math.Max(1, page) - 1) * pageSize };
        var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) FROM vehicles v {clause}", p, ct).ConfigureAwait(false);
        var items = await s.QueryAsync<VehicleEntity>($"SELECT {VehicleColumns} FROM vehicles v LEFT JOIN vehicle_categories c ON c.id = v.category_id {clause} ORDER BY v.registration_norm LIMIT @limit OFFSET @offset", p, ct).ConfigureAwait(false);
        return new PagedResult<VehicleEntity>(items.ToList(), total, page, pageSize);
    }

    public static Task<long> InsertVehicleAsync(DbSession s, VehicleEntity v, CancellationToken ct = default)
    {
        v.RegistrationNorm = VehicleRegistration.Normalize(v.Registration);
        v.Registration = VehicleRegistration.Display(v.Registration);
        return s.ExecuteScalarAsync<long>("""
            INSERT INTO vehicles (uid, registration, registration_norm, category_id, vehicle_type, make_model, axles, transport_company_id, default_driver_id,
                rfid_tag, qr_code, stored_tare_g, stored_tare_utc, stored_tare_approved_by, stored_tare_approved_utc, stored_tare_expires_utc,
                is_blacklisted, blacklist_reason, photo_path, notes, is_active, created_utc, updated_utc)
            VALUES (@Uid, @Registration, @RegistrationNorm, @CategoryId, @VehicleType, @MakeModel, @Axles, @TransportCompanyId, @DefaultDriverId,
                @RfidTag, @QrCode, @StoredTareG, @StoredTareUtc, @StoredTareApprovedBy, @StoredTareApprovedUtc, @StoredTareExpiresUtc,
                @IsBlacklisted, @BlacklistReason, @PhotoPath, @Notes, @IsActive, @CreatedUtc, @UpdatedUtc)
            RETURNING id
            """, v, ct)!;
    }

    public static Task<int> UpdateVehicleAsync(DbSession s, VehicleEntity v, CancellationToken ct = default)
    {
        v.RegistrationNorm = VehicleRegistration.Normalize(v.Registration);
        v.Registration = VehicleRegistration.Display(v.Registration);
        return s.ExecuteAsync("""
            UPDATE vehicles SET registration = @Registration, registration_norm = @RegistrationNorm, category_id = @CategoryId, vehicle_type = @VehicleType,
                make_model = @MakeModel, axles = @Axles, transport_company_id = @TransportCompanyId, default_driver_id = @DefaultDriverId, rfid_tag = @RfidTag,
                qr_code = @QrCode, is_blacklisted = @IsBlacklisted, blacklist_reason = @BlacklistReason, photo_path = @PhotoPath, notes = @Notes,
                is_active = @IsActive, updated_utc = @UpdatedUtc
            WHERE id = @Id
            """, v, ct);
    }

    /// <summary>Sets a stored tare. Unapproved tares (approvedBy null) cannot be used for weighing until approved.</summary>
    public static async Task SetStoredTareAsync(DbSession s, long vehicleId, long tareG, string source, long? transactionId, long createdBy, long? approvedBy,
        DateTimeOffset at, DateTimeOffset? expires, CancellationToken ct = default)
    {
        await s.ExecuteAsync("""
            UPDATE vehicles SET stored_tare_g = @tareG, stored_tare_utc = @at, stored_tare_approved_by = @approvedBy,
                stored_tare_approved_utc = CASE WHEN @approvedBy IS NULL THEN NULL ELSE @at END, stored_tare_expires_utc = @expires, updated_utc = @at
            WHERE id = @vehicleId
            """, new { vehicleId, tareG, approvedBy, at, expires }, ct).ConfigureAwait(false);
        await s.ExecuteAsync("""
            INSERT INTO vehicle_tare_history (vehicle_id, tare_g, source, transaction_id, approved_by, approved_utc, expires_utc, created_by, created_utc)
            VALUES (@vehicleId, @tareG, @source, @transactionId, @approvedBy, CASE WHEN @approvedBy IS NULL THEN NULL ELSE @at END, @expires, @createdBy, @at)
            """, new { vehicleId, tareG, source, transactionId, approvedBy, expires, createdBy, at }, ct).ConfigureAwait(false);
    }

    public static Task<int> ApproveStoredTareAsync(DbSession s, long vehicleId, long approvedBy, DateTimeOffset at, DateTimeOffset? expires, CancellationToken ct = default) =>
        s.ExecuteAsync("""
            UPDATE vehicles SET stored_tare_approved_by = @approvedBy, stored_tare_approved_utc = @at, stored_tare_expires_utc = @expires, updated_utc = @at
            WHERE id = @vehicleId AND stored_tare_g IS NOT NULL
            """, new { vehicleId, approvedBy, at, expires }, ct);

    public static async Task<IReadOnlyList<(long TareG, string Source, DateTimeOffset CreatedUtc, long? ApprovedBy)>> TareHistoryAsync(DbSession s, long vehicleId, CancellationToken ct = default) =>
        (await s.QueryAsync<(long, string, DateTimeOffset, long?)>("SELECT tare_g, source, created_utc, approved_by FROM vehicle_tare_history WHERE vehicle_id = @vehicleId ORDER BY id DESC",
            new { vehicleId }, ct).ConfigureAwait(false)).ToList();

    // ---------------------------------------------------------------- customers / suppliers

    private static string PartyTable(PartyKind kind) => kind == PartyKind.Customer ? "customers" : "suppliers";
    private const string PartyColumns = "id, uid, code, name, address, phone, email, tax_no, notes, is_active, created_utc, updated_utc";

    public static async Task<PagedResult<PartyEntity>> SearchPartiesAsync(DbSession s, PartyKind kind, string? text, int page = 1, int pageSize = 50, bool includeInactive = false, CancellationToken ct = default)
    {
        var table = PartyTable(kind);
        var where = new List<string>();
        if (!includeInactive) where.Add("is_active = @active");
        if (!string.IsNullOrWhiteSpace(text)) where.Add($"({s.Dialect.ContainsCi("name", "@like")} OR {s.Dialect.ContainsCi("COALESCE(code, '')", "@like")} OR {s.Dialect.ContainsCi("COALESCE(phone, '')", "@like")})");
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        var p = new { active = true, like = SqlDialect.LikePattern(text ?? string.Empty), limit = pageSize, offset = (Math.Max(1, page) - 1) * pageSize };
        var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) FROM {table} {clause}", p, ct).ConfigureAwait(false);
        var items = await s.QueryAsync<PartyEntity>($"SELECT {PartyColumns} FROM {table} {clause} ORDER BY name LIMIT @limit OFFSET @offset", p, ct).ConfigureAwait(false);
        return new PagedResult<PartyEntity>(items.ToList(), total, page, pageSize);
    }

    public static Task<PartyEntity?> PartyAsync(DbSession s, PartyKind kind, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<PartyEntity>($"SELECT {PartyColumns} FROM {PartyTable(kind)} WHERE id = @id", new { id }, ct);

    public static Task<long> InsertPartyAsync(DbSession s, PartyKind kind, PartyEntity e, CancellationToken ct = default) => s.ExecuteScalarAsync<long>($"""
        INSERT INTO {PartyTable(kind)} (uid, code, name, address, phone, email, tax_no, notes, is_active, created_utc, updated_utc)
        VALUES (@Uid, @Code, @Name, @Address, @Phone, @Email, @TaxNo, @Notes, @IsActive, @CreatedUtc, @UpdatedUtc) RETURNING id
        """, e, ct)!;

    public static Task<int> UpdatePartyAsync(DbSession s, PartyKind kind, PartyEntity e, CancellationToken ct = default) => s.ExecuteAsync($"""
        UPDATE {PartyTable(kind)} SET code = @Code, name = @Name, address = @Address, phone = @Phone, email = @Email, tax_no = @TaxNo, notes = @Notes,
            is_active = @IsActive, updated_utc = @UpdatedUtc WHERE id = @Id
        """, e, ct);

    // ---------------------------------------------------------------- materials

    private const string MaterialColumns = "id, uid, code, name, category, grade, unit, notes, is_active, created_utc, updated_utc";

    public static async Task<PagedResult<MaterialEntity>> SearchMaterialsAsync(DbSession s, string? text, int page = 1, int pageSize = 50, bool includeInactive = false, CancellationToken ct = default)
    {
        var where = new List<string>();
        if (!includeInactive) where.Add("is_active = @active");
        if (!string.IsNullOrWhiteSpace(text)) where.Add($"({s.Dialect.ContainsCi("name", "@like")} OR {s.Dialect.ContainsCi("COALESCE(code, '')", "@like")} OR {s.Dialect.ContainsCi("COALESCE(category, '')", "@like")})");
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        var p = new { active = true, like = SqlDialect.LikePattern(text ?? string.Empty), limit = pageSize, offset = (Math.Max(1, page) - 1) * pageSize };
        var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) FROM materials {clause}", p, ct).ConfigureAwait(false);
        var items = await s.QueryAsync<MaterialEntity>($"SELECT {MaterialColumns} FROM materials {clause} ORDER BY name LIMIT @limit OFFSET @offset", p, ct).ConfigureAwait(false);
        return new PagedResult<MaterialEntity>(items.ToList(), total, page, pageSize);
    }

    public static Task<MaterialEntity?> MaterialAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<MaterialEntity>($"SELECT {MaterialColumns} FROM materials WHERE id = @id", new { id }, ct);

    public static Task<long> InsertMaterialAsync(DbSession s, MaterialEntity e, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO materials (uid, code, name, category, grade, unit, notes, is_active, created_utc, updated_utc)
        VALUES (@Uid, @Code, @Name, @Category, @Grade, @Unit, @Notes, @IsActive, @CreatedUtc, @UpdatedUtc) RETURNING id
        """, e, ct)!;

    public static Task<int> UpdateMaterialAsync(DbSession s, MaterialEntity e, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE materials SET code = @Code, name = @Name, category = @Category, grade = @Grade, unit = @Unit, notes = @Notes, is_active = @IsActive,
            updated_utc = @UpdatedUtc WHERE id = @Id
        """, e, ct);

    // ---------------------------------------------------------------- drivers, transporters, categories

    public static async Task<IReadOnlyList<DriverEntity>> DriversAsync(DbSession s, string? text = null, CancellationToken ct = default) =>
        (await s.QueryAsync<DriverEntity>($"""
            SELECT id, uid, name, license_no, nic_no, phone, transport_company_id, is_active, created_utc, updated_utc FROM drivers
            WHERE is_active = @active {(string.IsNullOrWhiteSpace(text) ? "" : "AND (" + s.Dialect.ContainsCi("name", "@like") + " OR " + s.Dialect.ContainsCi("COALESCE(license_no, '')", "@like") + ")")}
            ORDER BY name LIMIT 500
            """, new { active = true, like = SqlDialect.LikePattern(text ?? string.Empty) }, ct).ConfigureAwait(false)).ToList();

    public static Task<long> InsertDriverAsync(DbSession s, DriverEntity e, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO drivers (uid, name, license_no, nic_no, phone, transport_company_id, is_active, created_utc, updated_utc)
        VALUES (@Uid, @Name, @LicenseNo, @NicNo, @Phone, @TransportCompanyId, @IsActive, @CreatedUtc, @UpdatedUtc) RETURNING id
        """, e, ct)!;

    public static Task<int> UpdateDriverAsync(DbSession s, DriverEntity e, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE drivers SET name = @Name, license_no = @LicenseNo, nic_no = @NicNo, phone = @Phone, transport_company_id = @TransportCompanyId,
            is_active = @IsActive, updated_utc = @UpdatedUtc WHERE id = @Id
        """, e, ct);

    public static async Task<IReadOnlyList<TransportCompanyEntity>> TransportCompaniesAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<TransportCompanyEntity>("SELECT id, uid, code, name, contact_person, phone, email, address, is_active, created_utc, updated_utc FROM transport_companies WHERE is_active = @active ORDER BY name",
            new { active = true }, ct).ConfigureAwait(false)).ToList();

    public static Task<long> InsertTransportCompanyAsync(DbSession s, TransportCompanyEntity e, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO transport_companies (uid, code, name, contact_person, phone, email, address, is_active, created_utc, updated_utc)
        VALUES (@Uid, @Code, @Name, @ContactPerson, @Phone, @Email, @Address, @IsActive, @CreatedUtc, @UpdatedUtc) RETURNING id
        """, e, ct)!;

    public static Task<int> UpdateTransportCompanyAsync(DbSession s, TransportCompanyEntity e, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE transport_companies SET code = @Code, name = @Name, contact_person = @ContactPerson, phone = @Phone, email = @Email, address = @Address,
            is_active = @IsActive, updated_utc = @UpdatedUtc WHERE id = @Id
        """, e, ct);

    public static async Task<IReadOnlyList<VehicleCategoryEntity>> CategoriesAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<VehicleCategoryEntity>("SELECT id, name, description FROM vehicle_categories ORDER BY name", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<long> InsertCategoryAsync(DbSession s, VehicleCategoryEntity e, CancellationToken ct = default) =>
        s.ExecuteScalarAsync<long>("INSERT INTO vehicle_categories (name, description) VALUES (@Name, @Description) RETURNING id", e, ct)!;

    // ---------------------------------------------------------------- orders

    private const string OrderColumns = "id, uid, order_type, order_no, customer_id, supplier_id, material_id, ordered_qty_g, status, valid_from_utc, valid_to_utc, notes, created_utc, updated_utc";

    public static async Task<IReadOnlyList<OrderEntity>> OpenOrdersAsync(DbSession s, CancellationToken ct = default) =>
        (await s.QueryAsync<OrderEntity>($"SELECT {OrderColumns} FROM orders WHERE status = 'Open' ORDER BY order_no", ct: ct).ConfigureAwait(false)).ToList();

    public static Task<OrderEntity?> OrderAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<OrderEntity>($"SELECT {OrderColumns} FROM orders WHERE id = @id", new { id }, ct);

    public static Task<long> InsertOrderAsync(DbSession s, OrderEntity e, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO orders (uid, order_type, order_no, customer_id, supplier_id, material_id, ordered_qty_g, status, valid_from_utc, valid_to_utc, notes, created_utc, updated_utc)
        VALUES (@Uid, @OrderType, @OrderNo, @CustomerId, @SupplierId, @MaterialId, @OrderedQtyG, @Status, @ValidFromUtc, @ValidToUtc, @Notes, @CreatedUtc, @UpdatedUtc) RETURNING id
        """, e, ct)!;

    public static Task<int> UpdateOrderAsync(DbSession s, OrderEntity e, CancellationToken ct = default) => s.ExecuteAsync("""
        UPDATE orders SET order_type = @OrderType, order_no = @OrderNo, customer_id = @CustomerId, supplier_id = @SupplierId, material_id = @MaterialId,
            ordered_qty_g = @OrderedQtyG, status = @Status, valid_from_utc = @ValidFromUtc, valid_to_utc = @ValidToUtc, notes = @Notes, updated_utc = @UpdatedUtc
        WHERE id = @Id
        """, e, ct);

    /// <summary>Net quantity already delivered against an order (completed transactions only).</summary>
    public static Task<long> DeliveredAgainstOrderAsync(DbSession s, long orderId, CancellationToken ct = default) =>
        s.QuerySingleAsync<long>("SELECT CAST(COALESCE(SUM(net_g), 0) AS BIGINT) FROM weighment_transactions WHERE order_id = @orderId AND status = 'Completed'", new { orderId }, ct);
}
