using System.Text;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Data.Stores;

public enum TransactionDateField { Created, Completed }

public enum TransactionSort { Created, Completed, TicketNo, Vehicle, Net }

/// <summary>History search. Every property is optional; weights are in kilograms.</summary>
public sealed record TransactionFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public TransactionDateField DateField { get; init; } = TransactionDateField.Created;
    public string? TicketNo { get; init; }
    public Guid? Uid { get; init; }
    public long? Id { get; init; }
    public string? Vehicle { get; init; }
    public long? CustomerId { get; init; }
    public long? SupplierId { get; init; }
    public long? MaterialId { get; init; }
    public long? OperatorId { get; init; }
    public long? SiteId { get; init; }
    public long? WeighbridgeId { get; init; }
    public TransactionStatus? Status { get; init; }
    public OperatingMode? Mode { get; init; }
    public TransactionDirection? Direction { get; init; }
    public bool? HasManualWeight { get; init; }
    public string? ProfileName { get; init; }
    public string? Text { get; init; }
    public decimal? FirstMinKg { get; init; }
    public decimal? FirstMaxKg { get; init; }
    public decimal? SecondMinKg { get; init; }
    public decimal? SecondMaxKg { get; init; }
    public decimal? GrossMinKg { get; init; }
    public decimal? GrossMaxKg { get; init; }
    public decimal? TareMinKg { get; init; }
    public decimal? TareMaxKg { get; init; }
    public decimal? NetMinKg { get; init; }
    public decimal? NetMaxKg { get; init; }
    public TransactionSort Sort { get; init; } = TransactionSort.Created;
    public bool Descending { get; init; } = true;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public static class TransactionStore
{
    private const string Columns = """
        t.id, t.uid, t.ticket_no, t.site_id, t.weighbridge_id, t.status, t.direction, t.operating_mode, t.vehicle_id, t.vehicle_reg, t.vehicle_reg_norm,
        t.driver_id, t.driver_name, t.transport_company_id, t.customer_id, t.customer_name, t.supplier_id, t.supplier_name, t.material_id, t.material_name,
        t.order_id, t.delivery_note_no, t.reference_no, t.remarks, t.replaces_transaction_id, t.first_measurement_id, t.second_measurement_id,
        t.first_g, t.second_g, t.gross_g, t.tare_g, t.net_g, t.first_is_gross, t.tare_method, t.has_manual_weight, t.has_override,
        t.created_by, u.display_name AS created_by_name, t.created_utc, t.completed_by, t.completed_utc, t.cancelled_by, t.cancelled_utc, t.cancel_reason,
        t.updated_utc, t.version, s.code AS site_code, w.code AS weighbridge_code
        """;

    private const string From = """
        FROM weighment_transactions t
        JOIN users u ON u.id = t.created_by
        JOIN sites s ON s.id = t.site_id
        JOIN weighbridges w ON w.id = t.weighbridge_id
        """;

    /// <summary>Atomically increments and returns a named counter (ticket numbers).</summary>
    public static Task<long> NextSequenceAsync(DbSession s, string scope, CancellationToken ct = default) => s.QuerySingleAsync<long>("""
        INSERT INTO sequences (scope, value) VALUES (@scope, 1)
        ON CONFLICT (scope) DO UPDATE SET value = sequences.value + 1
        RETURNING value
        """, new { scope }, ct);

    public static Task<long> InsertAsync(DbSession s, TransactionEntity t, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO weighment_transactions (uid, ticket_no, site_id, weighbridge_id, status, direction, operating_mode, vehicle_id, vehicle_reg, vehicle_reg_norm,
            driver_id, driver_name, transport_company_id, customer_id, customer_name, supplier_id, supplier_name, material_id, material_name, order_id,
            delivery_note_no, reference_no, remarks, replaces_transaction_id, has_manual_weight, has_override, created_by, created_utc, updated_utc, version)
        VALUES (@Uid, @TicketNo, @SiteId, @WeighbridgeId, @Status, @Direction, @Mode, @VehicleId, @VehicleReg, @VehicleRegNorm,
            @DriverId, @DriverName, @TransportCompanyId, @CustomerId, @CustomerName, @SupplierId, @SupplierName, @MaterialId, @MaterialName, @OrderId,
            @DeliveryNoteNo, @ReferenceNo, @Remarks, @ReplacesTransactionId, @HasManualWeight, @HasOverride, @CreatedBy, @CreatedUtc, @UpdatedUtc, 1)
        RETURNING id
        """, new
    {
        t.Uid, t.TicketNo, t.SiteId, t.WeighbridgeId, Status = t.Status.ToString(), Direction = t.Direction.ToString(), Mode = t.OperatingMode.ToString(),
        t.VehicleId, t.VehicleReg, t.VehicleRegNorm, t.DriverId, t.DriverName, t.TransportCompanyId, t.CustomerId, t.CustomerName, t.SupplierId, t.SupplierName,
        t.MaterialId, t.MaterialName, t.OrderId, t.DeliveryNoteNo, t.ReferenceNo, t.Remarks, t.ReplacesTransactionId, t.HasManualWeight, t.HasOverride,
        t.CreatedBy, t.CreatedUtc, t.UpdatedUtc,
    }, ct)!;

    public static Task<long> InsertMeasurementAsync(DbSession s, MeasurementEntity m, CancellationToken ct = default) => s.ExecuteScalarAsync<long>("""
        INSERT INTO weight_measurements (uid, transaction_id, stage, source, weight_g, reported_value, reported_unit, reported_decimals, is_stable,
            stability_reason, stability_span_g, stable_for_ms, unstable_override, override_reason, override_approved_by, manual_reason, manual_approved_by,
            captured_utc, reading_received_utc, reading_age_ms, raw_text, raw_hex, indicator_flags, port_description, profile_uid, profile_name,
            profile_version, operating_mode, operator_id, machine, corrects_measurement_id, correction_reason)
        VALUES (@Uid, @TransactionId, @Stage, @Source, @WeightG, @ReportedValue, @ReportedUnit, @ReportedDecimals, @IsStable,
            @StabilityReason, @StabilitySpanG, @StableForMs, @UnstableOverride, @OverrideReason, @OverrideApprovedBy, @ManualReason, @ManualApprovedBy,
            @CapturedUtc, @ReadingReceivedUtc, @ReadingAgeMs, @RawText, @RawHex, @IndicatorFlags, @PortDescription, @ProfileUid, @ProfileName,
            @ProfileVersion, @Mode, @OperatorId, @Machine, @CorrectsMeasurementId, @CorrectionReason)
        RETURNING id
        """, new
    {
        m.Uid, m.TransactionId, m.Stage, Source = m.Source.ToString(), m.WeightG, m.ReportedValue, m.ReportedUnit, m.ReportedDecimals, m.IsStable,
        m.StabilityReason, m.StabilitySpanG, m.StableForMs, m.UnstableOverride, m.OverrideReason, m.OverrideApprovedBy, m.ManualReason, m.ManualApprovedBy,
        m.CapturedUtc, m.ReadingReceivedUtc, m.ReadingAgeMs, m.RawText, m.RawHex, m.IndicatorFlags, m.PortDescription, m.ProfileUid, m.ProfileName,
        m.ProfileVersion, Mode = m.OperatingMode.ToString(), m.OperatorId, m.Machine, m.CorrectsMeasurementId, m.CorrectionReason,
    }, ct)!;

    public static async Task InsertRawFramesAsync(DbSession s, long measurementId, IEnumerable<RawFrameEntity> frames, CancellationToken ct = default)
    {
        foreach (var f in frames)
        {
            await s.ExecuteAsync("INSERT INTO raw_frames (measurement_id, seq, received_utc, hex, display, is_captured) VALUES (@measurementId, @Seq, @ReceivedUtc, @Hex, @Display, @IsCaptured)",
                new { measurementId, f.Seq, f.ReceivedUtc, f.Hex, f.Display, f.IsCaptured }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Links a first (or corrected first) measurement to a pending transaction.</summary>
    public static async Task SetFirstMeasurementAsync(DbSession s, long transactionId, long measurementId, long weightG, bool manual, bool overridden,
        DateTimeOffset at, int expectedVersion, CancellationToken ct = default)
    {
        var n = await s.ExecuteAsync("""
            UPDATE weighment_transactions SET first_measurement_id = @measurementId, first_g = @weightG,
                has_manual_weight = (CASE WHEN @manual THEN @yes ELSE has_manual_weight END),
                has_override = (CASE WHEN @overridden THEN @yes ELSE has_override END),
                updated_utc = @at, version = version + 1
            WHERE id = @transactionId AND status = 'Pending' AND version = @expectedVersion
            """, new { transactionId, measurementId, weightG, manual, overridden, yes = true, at, expectedVersion }, ct).ConfigureAwait(false);
        if (n != 1) throw new ConcurrencyException($"Transaction {transactionId} changed or is no longer pending.");
    }

    public sealed record Completion(
        long TransactionId,
        long? SecondMeasurementId,
        long? SecondG,
        long GrossG,
        long TareG,
        long NetG,
        bool FirstIsGross,
        TareMethod TareMethod,
        bool Manual,
        bool Overridden,
        long CompletedBy,
        DateTimeOffset CompletedUtc,
        int ExpectedVersion);

    public static async Task CompleteAsync(DbSession s, Completion c, CancellationToken ct = default)
    {
        var n = await s.ExecuteAsync("""
            UPDATE weighment_transactions SET status = 'Completed', second_measurement_id = @SecondMeasurementId, second_g = @SecondG,
                gross_g = @GrossG, tare_g = @TareG, net_g = @NetG, first_is_gross = @FirstIsGross, tare_method = @TareMethod,
                has_manual_weight = (CASE WHEN @Manual THEN @Yes ELSE has_manual_weight END),
                has_override = (CASE WHEN @Overridden THEN @Yes ELSE has_override END),
                completed_by = @CompletedBy, completed_utc = @CompletedUtc, updated_utc = @CompletedUtc, version = version + 1
            WHERE id = @TransactionId AND status = 'Pending' AND version = @ExpectedVersion
            """, new
        {
            c.TransactionId, c.SecondMeasurementId, c.SecondG, c.GrossG, c.TareG, c.NetG, c.FirstIsGross, TareMethod = c.TareMethod.ToString(),
            c.Manual, c.Overridden, Yes = true, c.CompletedBy, c.CompletedUtc, c.ExpectedVersion,
        }, ct).ConfigureAwait(false);
        if (n != 1) throw new ConcurrencyException($"Transaction {c.TransactionId} changed or is no longer pending.");
    }

    public static async Task CancelAsync(DbSession s, long transactionId, long userId, string reason, DateTimeOffset at, int expectedVersion, CancellationToken ct = default)
    {
        var n = await s.ExecuteAsync("""
            UPDATE weighment_transactions SET status = 'Cancelled', cancelled_by = @userId, cancelled_utc = @at, cancel_reason = @reason,
                updated_utc = @at, version = version + 1
            WHERE id = @transactionId AND status <> 'Cancelled' AND version = @expectedVersion
            """, new { transactionId, userId, reason, at, expectedVersion }, ct).ConfigureAwait(false);
        if (n != 1) throw new ConcurrencyException($"Transaction {transactionId} changed or is already cancelled.");
    }

    /// <summary>Editable details (never weights). Each change is written to transaction_changes by the caller.</summary>
    public static async Task UpdateDetailsAsync(DbSession s, TransactionEntity t, DateTimeOffset at, int expectedVersion, CancellationToken ct = default)
    {
        var n = await s.ExecuteAsync("""
            UPDATE weighment_transactions SET direction = @Direction, vehicle_id = @VehicleId, vehicle_reg = @VehicleReg, vehicle_reg_norm = @VehicleRegNorm,
                driver_id = @DriverId, driver_name = @DriverName, transport_company_id = @TransportCompanyId, customer_id = @CustomerId,
                customer_name = @CustomerName, supplier_id = @SupplierId, supplier_name = @SupplierName, material_id = @MaterialId,
                material_name = @MaterialName, order_id = @OrderId, delivery_note_no = @DeliveryNoteNo, reference_no = @ReferenceNo, remarks = @Remarks,
                updated_utc = @at, version = version + 1
            WHERE id = @Id AND status <> 'Cancelled' AND version = @expectedVersion
            """, new
        {
            t.Id, Direction = t.Direction.ToString(), t.VehicleId, t.VehicleReg, t.VehicleRegNorm, t.DriverId, t.DriverName, t.TransportCompanyId,
            t.CustomerId, t.CustomerName, t.SupplierId, t.SupplierName, t.MaterialId, t.MaterialName, t.OrderId, t.DeliveryNoteNo, t.ReferenceNo, t.Remarks,
            at, expectedVersion,
        }, ct).ConfigureAwait(false);
        if (n != 1) throw new ConcurrencyException($"Transaction {t.Id} changed or was cancelled.");
    }

    public static Task<int> AddChangeAsync(DbSession s, TransactionChangeEntity c, CancellationToken ct = default) => s.ExecuteAsync("""
        INSERT INTO transaction_changes (transaction_id, field, old_value, new_value, reason, user_id, approved_by, at_utc)
        VALUES (@TransactionId, @Field, @OldValue, @NewValue, @Reason, @UserId, @ApprovedBy, @AtUtc)
        """, c, ct);

    // ---------------------------------------------------------------- reads

    public static Task<TransactionEntity?> GetAsync(DbSession s, long id, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TransactionEntity>($"SELECT {Columns} {From} WHERE t.id = @id", new { id }, ct);

    public static Task<TransactionEntity?> GetByUidAsync(DbSession s, Guid uid, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TransactionEntity>($"SELECT {Columns} {From} WHERE t.uid = @uid", new { uid }, ct);

    public static Task<TransactionEntity?> GetByTicketAsync(DbSession s, string ticketNo, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TransactionEntity>($"SELECT {Columns} {From} WHERE t.ticket_no = @ticketNo", new { ticketNo }, ct);

    public static Task<TransactionEntity?> PendingForVehicleAsync(DbSession s, long siteId, string registration, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TransactionEntity>($"SELECT {Columns} {From} WHERE t.site_id = @siteId AND t.vehicle_reg_norm = @norm AND t.status = 'Pending'",
            new { siteId, norm = VehicleRegistration.Normalize(registration) }, ct);

    public static Task<TransactionEntity?> ReplacementOfAsync(DbSession s, long transactionId, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TransactionEntity>($"SELECT {Columns} {From} WHERE t.replaces_transaction_id = @transactionId ORDER BY t.id DESC LIMIT 1", new { transactionId }, ct);

    public static Task<int> PrintCountAsync(DbSession s, long transactionId, CancellationToken ct = default) =>
        s.QuerySingleAsync<int>("SELECT CAST(COUNT(*) AS INTEGER) FROM print_history WHERE transaction_id = @transactionId AND action IN ('Print', 'Reprint')", new { transactionId }, ct);

    public static async Task<IReadOnlyList<TransactionEntity>> PendingAsync(DbSession s, long? siteId = null, CancellationToken ct = default) =>
        (await s.QueryAsync<TransactionEntity>($"SELECT {Columns} {From} WHERE t.status = 'Pending' {(siteId is null ? "" : "AND t.site_id = @siteId")} ORDER BY t.created_utc",
            new { siteId }, ct).ConfigureAwait(false)).ToList();

    /// <summary>The latest completed transaction of a vehicle after <paramref name="since"/> (duplicate detection).</summary>
    public static Task<TransactionEntity?> LastCompletedForVehicleAsync(DbSession s, string registration, DateTimeOffset since, CancellationToken ct = default) =>
        s.QuerySingleOrDefaultAsync<TransactionEntity>($"""
            SELECT {Columns} {From} WHERE t.vehicle_reg_norm = @norm AND t.status = 'Completed' AND t.completed_utc >= @since ORDER BY t.completed_utc DESC LIMIT 1
            """, new { norm = VehicleRegistration.Normalize(registration), since }, ct);

    public static async Task<IReadOnlyList<TransactionEntity>> RecentAsync(DbSession s, int count = 20, long? weighbridgeId = null, CancellationToken ct = default) =>
        (await s.QueryAsync<TransactionEntity>($"SELECT {Columns} {From} {(weighbridgeId is null ? "" : "WHERE t.weighbridge_id = @weighbridgeId")} ORDER BY t.updated_utc DESC, t.id DESC LIMIT @count",
            new { count, weighbridgeId }, ct).ConfigureAwait(false)).ToList();

    public static async Task<IReadOnlyList<MeasurementEntity>> MeasurementsAsync(DbSession s, long transactionId, CancellationToken ct = default) =>
        (await s.QueryAsync<MeasurementEntity>("""
            SELECT m.id, m.uid, m.transaction_id, m.stage, m.source, m.weight_g, m.reported_value, m.reported_unit, m.reported_decimals, m.is_stable,
                m.stability_reason, m.stability_span_g, m.stable_for_ms, m.unstable_override, m.override_reason, m.override_approved_by, m.manual_reason,
                m.manual_approved_by, m.captured_utc, m.reading_received_utc, m.reading_age_ms, m.raw_text, m.raw_hex, m.indicator_flags, m.port_description,
                m.profile_uid, m.profile_name, m.profile_version, m.operating_mode, m.operator_id, u.display_name AS operator_name, m.machine,
                m.corrects_measurement_id, m.correction_reason
            FROM weight_measurements m JOIN users u ON u.id = m.operator_id WHERE m.transaction_id = @transactionId ORDER BY m.id
            """, new { transactionId }, ct).ConfigureAwait(false)).ToList();

    public static async Task<IReadOnlyList<RawFrameEntity>> RawFramesAsync(DbSession s, long measurementId, CancellationToken ct = default) =>
        (await s.QueryAsync<RawFrameEntity>("SELECT id, measurement_id, seq, received_utc, hex, display, is_captured FROM raw_frames WHERE measurement_id = @measurementId ORDER BY seq",
            new { measurementId }, ct).ConfigureAwait(false)).ToList();

    public static async Task<IReadOnlyList<TransactionChangeEntity>> ChangesAsync(DbSession s, long transactionId, CancellationToken ct = default) =>
        (await s.QueryAsync<TransactionChangeEntity>("""
            SELECT c.id, c.transaction_id, c.field, c.old_value, c.new_value, c.reason, c.user_id, u.display_name AS user_name, c.approved_by, c.at_utc
            FROM transaction_changes c JOIN users u ON u.id = c.user_id WHERE c.transaction_id = @transactionId ORDER BY c.id
            """, new { transactionId }, ct).ConfigureAwait(false)).ToList();

    public static Task<int> AddPrintAsync(DbSession s, PrintHistoryEntity p, CancellationToken ct = default) => s.ExecuteAsync("""
        INSERT INTO print_history (transaction_id, action, copies, printer, template, reason, user_id, at_utc)
        VALUES (@TransactionId, @Action, @Copies, @Printer, @Template, @Reason, @UserId, @AtUtc)
        """, p, ct);

    public static async Task<IReadOnlyList<PrintHistoryEntity>> PrintsAsync(DbSession s, long transactionId, CancellationToken ct = default) =>
        (await s.QueryAsync<PrintHistoryEntity>("""
            SELECT p.id, p.transaction_id, p.action, p.copies, p.printer, p.template, p.reason, p.user_id, u.display_name AS user_name, p.at_utc
            FROM print_history p JOIN users u ON u.id = p.user_id WHERE p.transaction_id = @transactionId ORDER BY p.id
            """, new { transactionId }, ct).ConfigureAwait(false)).ToList();

    public static Task<int> AddEvidenceAsync(DbSession s, EvidenceEntity e, CancellationToken ct = default) => s.ExecuteAsync("""
        INSERT INTO camera_evidence (uid, transaction_id, measurement_id, camera_id, camera_name, file_path, sha256, content_type, size_bytes, captured_utc)
        VALUES (@Uid, @TransactionId, @MeasurementId, @CameraId, @CameraName, @FilePath, @Sha256, @ContentType, @SizeBytes, @CapturedUtc)
        """, e, ct);

    public static async Task<IReadOnlyList<EvidenceEntity>> EvidenceAsync(DbSession s, long transactionId, CancellationToken ct = default) =>
        (await s.QueryAsync<EvidenceEntity>("""
            SELECT id, uid, transaction_id, measurement_id, camera_id, camera_name, file_path, sha256, content_type, size_bytes, captured_utc
            FROM camera_evidence WHERE transaction_id = @transactionId ORDER BY id
            """, new { transactionId }, ct).ConfigureAwait(false)).ToList();

    // ---------------------------------------------------------------- search

    public static async Task<PagedResult<TransactionEntity>> SearchAsync(DbSession s, TransactionFilter f, CancellationToken ct = default)
    {
        var (where, p) = BuildWhere(s, f);
        var pageSize = Math.Clamp(f.PageSize, 1, 10_000);
        var page = Math.Max(1, f.Page);
        p.Add("limit", pageSize);
        p.Add("offset", (page - 1) * pageSize);
        var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) {From} {where}", p, ct).ConfigureAwait(false);
        var items = await s.QueryAsync<TransactionEntity>($"SELECT {Columns} {From} {where} ORDER BY {OrderBy(f)} LIMIT @limit OFFSET @offset", p, ct).ConfigureAwait(false);
        return new PagedResult<TransactionEntity>(items.ToList(), total, page, pageSize);
    }

    /// <summary>All matching rows (exports and reports), streamed in pages to bound memory.</summary>
    public static async IAsyncEnumerable<TransactionEntity> StreamAsync(DbSession s, TransactionFilter f, int maxRows = 1_000_000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        const int chunk = 2000;
        var emitted = 0;
        for (var page = 1; emitted < maxRows; page++)
        {
            var result = await SearchAsync(s, f with { Page = page, PageSize = chunk }, ct).ConfigureAwait(false);
            foreach (var t in result.Items)
            {
                if (emitted++ >= maxRows) yield break;
                yield return t;
            }
            if (result.Items.Count < chunk) yield break;
        }
    }

    private static string OrderBy(TransactionFilter f)
    {
        var dir = f.Descending ? "DESC" : "ASC";
        return f.Sort switch
        {
            TransactionSort.Completed => $"t.completed_utc {dir}, t.id {dir}",
            TransactionSort.TicketNo => $"t.ticket_no {dir}",
            TransactionSort.Vehicle => $"t.vehicle_reg_norm {dir}, t.id {dir}",
            TransactionSort.Net => $"t.net_g {dir}, t.id {dir}",
            _ => $"t.created_utc {dir}, t.id {dir}",
        };
    }

    private static (string Where, Dapper.DynamicParameters Params) BuildWhere(DbSession s, TransactionFilter f)
    {
        var w = new List<string>();
        var p = new Dapper.DynamicParameters();
        var dateCol = f.DateField == TransactionDateField.Completed ? "t.completed_utc" : "t.created_utc";
        if (f.From is { } from) { w.Add($"{dateCol} >= @from"); p.Add("from", Iso.ToText(from)); }
        if (f.To is { } to) { w.Add($"{dateCol} < @to"); p.Add("to", Iso.ToText(to)); }
        if (f.Id is { } id) { w.Add("t.id = @id"); p.Add("id", id); }
        if (f.Uid is { } uid) { w.Add("t.uid = @uid"); p.Add("uid", uid.ToString("D")); }
        if (!string.IsNullOrWhiteSpace(f.TicketNo)) { w.Add(s.Dialect.ContainsCi("t.ticket_no", "@ticket")); p.Add("ticket", SqlDialect.LikePattern(f.TicketNo.Trim())); }
        if (!string.IsNullOrWhiteSpace(f.Vehicle)) { w.Add("t.vehicle_reg_norm LIKE @vehicle ESCAPE '\\'"); p.Add("vehicle", SqlDialect.LikePattern(VehicleRegistration.Normalize(f.Vehicle))); }
        if (f.CustomerId is { } c) { w.Add("t.customer_id = @customer"); p.Add("customer", c); }
        if (f.SupplierId is { } sup) { w.Add("t.supplier_id = @supplier"); p.Add("supplier", sup); }
        if (f.MaterialId is { } mat) { w.Add("t.material_id = @material"); p.Add("material", mat); }
        if (f.OperatorId is { } op) { w.Add("t.created_by = @operator"); p.Add("operator", op); }
        if (f.SiteId is { } site) { w.Add("t.site_id = @site"); p.Add("site", site); }
        if (f.WeighbridgeId is { } wb) { w.Add("t.weighbridge_id = @wb"); p.Add("wb", wb); }
        if (f.Status is { } st) { w.Add("t.status = @status"); p.Add("status", st.ToString()); }
        if (f.Mode is { } mode) { w.Add("t.operating_mode = @mode"); p.Add("mode", mode.ToString()); }
        if (f.Direction is { } dir) { w.Add("t.direction = @direction"); p.Add("direction", dir.ToString()); }
        if (f.HasManualWeight is { } manual) { w.Add("t.has_manual_weight = @manual"); p.Add("manual", manual); }
        if (!string.IsNullOrWhiteSpace(f.ProfileName))
        {
            w.Add($"EXISTS (SELECT 1 FROM weight_measurements m WHERE m.transaction_id = t.id AND {s.Dialect.ContainsCi("COALESCE(m.profile_name, '')", "@profile")})");
            p.Add("profile", SqlDialect.LikePattern(f.ProfileName));
        }
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var cols = new[] { "t.ticket_no", "t.vehicle_reg", "COALESCE(t.customer_name, '')", "COALESCE(t.supplier_name, '')", "COALESCE(t.material_name, '')",
                "COALESCE(t.reference_no, '')", "COALESCE(t.delivery_note_no, '')", "COALESCE(t.remarks, '')", "COALESCE(t.driver_name, '')" };
            w.Add("(" + string.Join(" OR ", cols.Select(col => s.Dialect.ContainsCi(col, "@text"))) + ")");
            p.Add("text", SqlDialect.LikePattern(f.Text.Trim()));
        }
        Range(w, p, "t.first_g", "first", f.FirstMinKg, f.FirstMaxKg);
        Range(w, p, "t.second_g", "second", f.SecondMinKg, f.SecondMaxKg);
        Range(w, p, "t.gross_g", "gross", f.GrossMinKg, f.GrossMaxKg);
        Range(w, p, "t.tare_g", "tare", f.TareMinKg, f.TareMaxKg);
        Range(w, p, "t.net_g", "net", f.NetMinKg, f.NetMaxKg);
        return (w.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", w), p);
    }

    private static void Range(List<string> w, Dapper.DynamicParameters p, string column, string name, decimal? minKg, decimal? maxKg)
    {
        if (minKg is { } min) { w.Add($"{column} >= @{name}Min"); p.Add(name + "Min", (long)Math.Round(min * 1000m)); }
        if (maxKg is { } max) { w.Add($"{column} <= @{name}Max"); p.Add(name + "Max", (long)Math.Round(max * 1000m)); }
    }

    public static string Describe(TransactionFilter f)
    {
        var sb = new StringBuilder();
        if (f.From is { } a) sb.Append("from ").Append(a.ToString("yyyy-MM-dd HH:mm")).Append(' ');
        if (f.To is { } b) sb.Append("to ").Append(b.ToString("yyyy-MM-dd HH:mm")).Append(' ');
        if (f.Status is { } st) sb.Append("status ").Append(st).Append(' ');
        if (!string.IsNullOrWhiteSpace(f.Vehicle)) sb.Append("vehicle ").Append(f.Vehicle).Append(' ');
        if (!string.IsNullOrWhiteSpace(f.Text)) sb.Append("text '").Append(f.Text).Append("' ");
        return sb.Length == 0 ? "all records" : sb.ToString().Trim();
    }
}
