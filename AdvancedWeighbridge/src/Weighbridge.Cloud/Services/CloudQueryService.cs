using System.Globalization;
using System.Text;
using System.Text.Json;
using Weighbridge.Contracts;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Reporting.Reports;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Cloud.Services;

public sealed record TransactionQuery
{
    public string? Site { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Q { get; init; }
    public string? Status { get; init; }
    public string? Vehicle { get; init; }
    public bool ManualOnly { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Read side of the cloud for the web dashboard and the mobile app. Every query is limited to the sites the user may see.</summary>
public sealed class CloudQueryService(Database db, CloudOptions options, TimeProvider time)
{
    private const string TxColumns = """
        t.id, t.site_id, s.code AS site_code, t.uid, t.ticket_no, t.version, t.status, t.vehicle_reg, t.customer_name, t.supplier_name, t.material_name,
        t.gross_g, t.tare_g, t.net_g, t.has_manual, t.operating_mode, t.created_utc, t.completed_utc, t.payload_json, t.payload_hash
        """;

    // ------------------------------------------------------------------ sites and live status

    public Task<List<SiteDto>> SitesAsync(CloudPrincipal user, CancellationToken ct = default) => db.ReadAsync(async s =>
        (await CloudStore.SitesAsync(s, user.SiteIds, ct).ConfigureAwait(false)).Where(x => x.IsActive).Select(ToSiteDto).ToList(), ct);

    public Task<List<SiteStatusDto>> StatusAsync(CloudPrincipal user, string? siteCode, CancellationToken ct = default) => db.ReadAsync(async s =>
    {
        var sites = (await CloudStore.SitesAsync(s, user.SiteIds, ct).ConfigureAwait(false)).Where(x => x.IsActive);
        if (siteCode is not null)
        {
            sites = sites.Where(x => x.Code.Equals(siteCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!sites.Any()) throw new KeyNotFoundException($"Site '{siteCode}' not found.");
        }
        return sites.Select(ToStatus).ToList();
    }, ct);

    /// <summary>
    /// Live values are only as fresh as the last heartbeat. When it is older than the configured limit, the status is flagged
    /// as not live and clients must show it as "last known" with its age, never as the current weight.
    /// </summary>
    private SiteStatusDto ToStatus(SiteRow site)
    {
        var now = time.GetUtcNow();
        HeartbeatRequest? hb = null;
        if (site.HeartbeatJson is { Length: > 0 } json)
        {
            try { hb = JsonSerializer.Deserialize<HeartbeatRequest>(json, Api.Json); }
            catch (JsonException) { }
        }
        double? age = site.LastHeartbeatUtc is { } last ? Math.Max(0, (now - last).TotalSeconds) : null;
        var stale = age is null || age > options.LiveDataStaleSeconds;
        var weighbridges = (hb?.Weighbridges ?? []).Select(w =>
        {
            // The weight's own timestamp counts too: a site may keep sending heartbeats while its indicator is silent.
            var weightAge = w.MeasuredUtc is { } m ? (now - m).TotalSeconds : double.MaxValue;
            return weightAge > options.LiveDataStaleSeconds + 5 && w.LiveG is not null && !stale ? w with { DisplayStatus = "NotLive" } : w;
        }).ToList();
        return new SiteStatusDto(ToSiteDto(site), weighbridges, hb?.Health, age is null ? null : Math.Round(age.Value, 1), stale);
    }

    private SiteDto ToSiteDto(SiteRow s) => new(s.Id, s.Code, s.Name, s.LastHeartbeatUtc, IsOnline(s), s.AppVersion);

    public bool IsOnline(SiteRow s) => s.LastHeartbeatUtc is { } last && time.GetUtcNow() - last <= TimeSpan.FromMinutes(options.SiteOfflineAfterMinutes);

    // ------------------------------------------------------------------ transactions

    public async Task<PagedDto<TransactionSummaryDto>> TransactionsAsync(CloudPrincipal user, TransactionQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        return await db.ReadAsync(async s =>
        {
            var (where, args) = await FilterAsync(s, user, q, ct).ConfigureAwait(false);
            args.Add("limit", size);
            args.Add("offset", (page - 1) * size);
            var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) FROM cloud_transactions t JOIN sites s ON s.id = t.site_id WHERE {where}", args, ct).ConfigureAwait(false);
            var rows = await s.QueryAsync<CloudTransactionRow>(
                $"SELECT {TxColumns} FROM cloud_transactions t JOIN sites s ON s.id = t.site_id WHERE {where} ORDER BY t.created_utc DESC, t.id DESC LIMIT @limit OFFSET @offset", args, ct).ConfigureAwait(false);
            return new PagedDto<TransactionSummaryDto>(rows.Select(Summary).ToList(), total, page, size);
        }, ct).ConfigureAwait(false);
    }

    private static async Task<(string Where, Dapper.DynamicParameters Args)> FilterAsync(DbSession s, CloudPrincipal user, TransactionQuery q, CancellationToken ct)
    {
        var where = new List<string>();
        var args = new Dapper.DynamicParameters();
        if (user.SiteIds is not null)
        {
            where.Add(s.Dialect.InList("t.site_id", "@sites"));
            args.Add("sites", user.SiteIds.DefaultIfEmpty(-1).ToArray());
        }
        if (!string.IsNullOrWhiteSpace(q.Site))
        {
            var site = await CloudStore.SiteByCodeAsync(s, q.Site.Trim(), ct).ConfigureAwait(false);
            where.Add("t.site_id = @siteId");
            args.Add("siteId", site?.Id ?? -1);
        }
        if (q.From is { } from) { where.Add("t.created_utc >= @from"); args.Add("from", Iso.ToText(from)); }
        if (q.To is { } to) { where.Add("t.created_utc < @to"); args.Add("to", Iso.ToText(to)); }
        if (!string.IsNullOrWhiteSpace(q.Status)) { where.Add("t.status = @status"); args.Add("status", q.Status.Trim()); }
        if (!string.IsNullOrWhiteSpace(q.Vehicle)) { where.Add("t.vehicle_norm = @vehicle"); args.Add("vehicle", VehicleRegistration.Normalize(q.Vehicle)); }
        if (q.ManualOnly) { where.Add("t.has_manual = @manual"); args.Add("manual", true); }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var d = s.Dialect;
            where.Add($"({d.ContainsCi("t.ticket_no", "@q")} OR {d.ContainsCi("t.vehicle_reg", "@q")} OR t.vehicle_norm LIKE @qnorm ESCAPE '\\' OR {d.ContainsCi("t.customer_name", "@q")} OR {d.ContainsCi("t.supplier_name", "@q")} OR {d.ContainsCi("t.material_name", "@q")})");
            args.Add("q", SqlDialect.LikePattern(q.Q.Trim()));
            args.Add("qnorm", SqlDialect.LikePattern(VehicleRegistration.Normalize(q.Q)));
        }
        return (where.Count == 0 ? "1 = 1" : string.Join(" AND ", where), args);
    }

    public Task<TransactionDetailDto> DetailAsync(CloudPrincipal user, Guid uid, CancellationToken ct = default) => db.ReadAsync(async s =>
    {
        var row = await RowAsync(s, user, uid, ct).ConfigureAwait(false);
        var commands = (await s.QueryAsync<CommandRow>("""
            SELECT id, uid, site_id, type, target_uid, reason, requested_by, requested_utc, status, result_message, delivered_utc, completed_utc
            FROM commands WHERE target_uid = @uid ORDER BY id
            """, new { uid = row.Uid }, ct).ConfigureAwait(false)).Select(c => c.ToDto()).ToList();
        return new TransactionDetailDto(Summary(row), Payload(row), commands);
    }, ct);

    /// <summary>A PDF copy rendered from the synchronised record. It is marked as a cloud copy; the original is printed at the site.</summary>
    public async Task<(byte[] Pdf, string FileName)> TicketPdfAsync(CloudPrincipal user, Guid uid, CancellationToken ct = default)
    {
        var (row, site) = await db.ReadAsync(async s =>
        {
            var r = await RowAsync(s, user, uid, ct).ConfigureAwait(false);
            return (r, await CloudStore.SiteAsync(s, r.SiteId, ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);
        var t = Payload(row);
        var first = t.Measurements.Where(m => m.Stage == 1).LastOrDefault();
        var second = t.Measurements.Where(m => m.Stage == 2).LastOrDefault();
        static TicketWeighing? W(SyncMeasurementDto? m) => m is null ? null : new TicketWeighing(Weight.FromGrams(m.WeightG), m.CapturedUtc,
            m.Source == "Manual" ? "Manual entry" : m.Source == "StoredTare" ? "Stored tare" : m.UnstableOverride ? "Indicator, override" : m.IsStable ? "Indicator, stable" : "Indicator",
            m.Source == "Manual", m.IsStable);
        var data = new TicketData
        {
            Company = new CompanyInfo { Name = site?.Name ?? t.SiteCode },
            TicketNo = t.TicketNo,
            TransactionUid = t.Uid,
            Status = t.Status switch { "Pending" => TicketStatus.Pending, "Cancelled" => TicketStatus.Cancelled, _ => TicketStatus.Completed },
            SiteName = site?.Name,
            WeighbridgeName = t.WeighbridgeCode,
            VehicleReg = t.VehicleReg,
            DriverName = t.DriverName,
            Customer = t.CustomerName,
            Supplier = t.SupplierName,
            Material = t.MaterialName,
            OrderNo = t.OrderNo,
            DeliveryNote = t.DeliveryNoteNo,
            Reference = t.ReferenceNo,
            Direction = t.Direction,
            Remarks = t.Remarks,
            First = W(first),
            Second = W(second),
            Gross = t.GrossG is { } g ? Weight.FromGrams(g) : null,
            Tare = t.TareG is { } ta ? Weight.FromGrams(ta) : null,
            Net = t.NetG is { } n ? Weight.FromGrams(n) : null,
            TareMethodLabel = t.TareMethod switch { "StoredTare" => "stored tare", "ManualTare" => "manual tare", "Measured" => "measured", _ => null },
            OperatorName = t.CompletedBy ?? t.CreatedBy,
            ModeLabel = t.OperatingMode switch { "Automatic" => "Automatic", "Manual" => "Manual", _ => "Semi-automatic" },
            HasManualWeight = t.HasManualWeight,
            HasOverride = t.HasOverride,
            CancelReason = t.CancelReason,
            PrintedAt = time.GetUtcNow(),
        };
        var template = new TicketTemplate
        {
            Paper = PaperSize.A4, Bilingual = false, ShowLogo = false, ShowQrCode = false, ShowImages = false,
            Footer = $"Copy downloaded from AWMS Cloud on {TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.TimeZoneInfo):yyyy-MM-dd HH:mm} — the original ticket is issued at the site.",
            TimeZoneId = options.TimeZone,
        };
        return (TicketRenderer.RenderPdf(data, template), $"{SafeFileName(t.TicketNo)}.pdf");
    }

    /// <summary>Queues a cancellation for the site. The site applies it with its own rules and reports the outcome.</summary>
    public async Task<RemoteCommandDto> RequestCancelAsync(CloudPrincipal user, Guid uid, string? reason, string? ip, CancellationToken ct = default)
    {
        if (!user.Permissions.Contains(Permissions.TransactionCancel)) throw new UnauthorizedAccessException("You do not have the permission to cancel transactions.");
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length < 5) throw new ArgumentException("Enter a reason of at least 5 characters.");
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var row = await RowAsync(s, user, uid, ct).ConfigureAwait(false);
            if (row.Status == nameof(TransactionStatus.Cancelled)) throw new InvalidOperationException($"Ticket {row.TicketNo} is already cancelled.");
            var open = await s.QuerySingleOrDefaultAsync<CommandRow>("""
                SELECT id, uid, site_id, type, target_uid, reason, requested_by, requested_utc, status, result_message, delivered_utc, completed_utc
                FROM commands WHERE target_uid = @Uid AND type = @type AND status IN ('Pending', 'Delivered')
                """, new { row.Uid, type = CommandTypes.CancelTransaction }, ct).ConfigureAwait(false);
            if (open is not null) return open.ToDto();
            var command = new CommandRow
            {
                Uid = Guid.NewGuid().ToString("D"), SiteId = row.SiteId, Type = CommandTypes.CancelTransaction, TargetUid = row.Uid, Reason = CloudStore.Clip(reason, 500),
                RequestedBy = user.Username, RequestedUtc = now, Status = "Pending",
            };
            command.Id = await s.QuerySingleAsync<long>("""
                INSERT INTO commands (uid, site_id, type, target_uid, reason, requested_by, requested_utc, status)
                VALUES (@Uid, @SiteId, @Type, @TargetUid, @Reason, @RequestedBy, @RequestedUtc, @Status) RETURNING id
                """, command, ct).ConfigureAwait(false);
            await CloudStore.ServerAuditAsync(s, now, user.Username, "CancelRequested", $"Ticket {row.TicketNo} ({row.SiteCode}): {reason}", ip, ct).ConfigureAwait(false);
            return command.ToDto();
        }, ct).ConfigureAwait(false);
    }

    private static async Task<CloudTransactionRow> RowAsync(DbSession s, CloudPrincipal user, Guid uid, CancellationToken ct)
    {
        var row = await s.QuerySingleOrDefaultAsync<CloudTransactionRow>($"SELECT {TxColumns} FROM cloud_transactions t JOIN sites s ON s.id = t.site_id WHERE t.uid = @uid",
            new { uid = uid.ToString("D") }, ct).ConfigureAwait(false);
        if (row is null || !user.CanAccess(row.SiteId)) throw new KeyNotFoundException("Transaction not found.");
        return row;
    }

    private static SyncTransactionDto Payload(CloudTransactionRow r) => JsonSerializer.Deserialize<SyncTransactionDto>(r.PayloadJson, Api.Json)!;

    private static TransactionSummaryDto Summary(CloudTransactionRow r) => new(Guid.Parse(r.Uid), r.TicketNo, r.SiteCode, r.Status, r.VehicleReg, r.CustomerName, r.SupplierName,
        r.MaterialName, r.GrossG, r.TareG, r.NetG, r.HasManual, r.CreatedUtc, r.CompletedUtc, r.OperatingMode);

    // ------------------------------------------------------------------ reports

    public static readonly string[] Groupings = ["day", "site", "customer", "supplier", "material", "vehicle"];

    /// <summary>Completed transactions in [from, to) by completion time, grouped. Days follow the configured time zone.</summary>
    public async Task<SummaryReportDto> SummaryAsync(CloudPrincipal user, string groupBy, DateTimeOffset from, DateTimeOffset to, string? site, CancellationToken ct = default)
    {
        groupBy = (groupBy ?? "day").ToLowerInvariant();
        if (!Groupings.Contains(groupBy)) throw new ArgumentException($"groupBy must be one of: {string.Join(", ", Groupings)}.");
        if (to <= from) throw new ArgumentException("'to' must be after 'from'.");
        if (to - from > TimeSpan.FromDays(366)) throw new ArgumentException("The report period is limited to one year.");
        var rows = await CompletedAsync(user, from, to, site, ct).ConfigureAwait(false);
        var tz = options.TimeZoneInfo;
        string Key(CloudTransactionRow r) => groupBy switch
        {
            "day" => TimeZoneInfo.ConvertTime(r.CompletedUtc ?? r.CreatedUtc, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "site" => r.SiteCode,
            "customer" => r.CustomerName ?? "(none)",
            "supplier" => r.SupplierName ?? "(none)",
            "material" => r.MaterialName ?? "(none)",
            _ => r.VehicleReg,
        };
        var groups = rows.GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SummaryRowDto(g.Key, g.LongCount(), g.Sum(r => r.NetG ?? 0)))
            .OrderBy(r => groupBy == "day" ? r.Key : null, StringComparer.Ordinal)
            .ThenByDescending(r => groupBy == "day" ? 0 : r.NetG)
            .ToList();
        return new SummaryReportDto(groupBy, from, to, groups, rows.Count, rows.Sum(r => r.NetG ?? 0));
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> ExportAsync(CloudPrincipal user, string format, DateTimeOffset from, DateTimeOffset to, string? site, CancellationToken ct = default)
    {
        if (!user.Permissions.Contains(Permissions.ReportsExport)) throw new UnauthorizedAccessException("You do not have the permission to export reports.");
        if (to <= from || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a period of up to one year.");
        var rows = await CompletedAsync(user, from, to, site, ct).ConfigureAwait(false);
        var tz = options.TimeZoneInfo;
        var table = new ReportTable
        {
            Title = "Completed weighments",
            Subtitle = $"{TimeZoneInfo.ConvertTime(from, tz):yyyy-MM-dd HH:mm} – {TimeZoneInfo.ConvertTime(to, tz):yyyy-MM-dd HH:mm}{(site is null ? string.Empty : " · site " + site)}",
            CompanyName = "AWMS Cloud",
            GeneratedAt = time.GetUtcNow(),
            GeneratedBy = user.DisplayName,
            TimeZone = tz,
            Landscape = true,
            Columns =
            [
                new("Ticket", ColumnKind.Text, 1.1f), new("Site", ColumnKind.Text, 0.6f), new("Completed", ColumnKind.DateTime, 1.2f), new("Vehicle", ColumnKind.Text, 1.1f),
                new("Customer", ColumnKind.Text, 1.4f), new("Supplier", ColumnKind.Text, 1.2f), new("Material", ColumnKind.Text, 1.2f), new("Gross", ColumnKind.Weight),
                new("Tare", ColumnKind.Weight), new("Net", ColumnKind.Weight), new("Mode", ColumnKind.Text, 0.9f), new("Manual", ColumnKind.Text, 0.6f),
            ],
            Rows = rows.OrderBy(r => r.CompletedUtc).Select(r => (IReadOnlyList<object?>)
            [
                r.TicketNo, r.SiteCode, r.CompletedUtc, r.VehicleReg, r.CustomerName, r.SupplierName, r.MaterialName,
                r.GrossG is { } g ? Weight.FromGrams(g) : null, r.TareG is { } ta ? Weight.FromGrams(ta) : null, r.NetG is { } n ? Weight.FromGrams(n) : null,
                r.OperatingMode, r.HasManual ? "Yes" : string.Empty,
            ]).ToList(),
            Totals = [$"{rows.Count} tickets", null, null, null, null, null, null, null, null, Weight.FromGrams(rows.Sum(r => r.NetG ?? 0)), null, null],
        };
        var stamp = TimeZoneInfo.ConvertTime(from, tz).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + TimeZoneInfo.ConvertTime(to, tz).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return format.ToLowerInvariant() switch
        {
            "csv" => (CsvExporter.ToBytes(table), "text/csv; charset=utf-8", $"weighments-{stamp}.csv"),
            "xlsx" => (XlsxExporter.ToBytes(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"weighments-{stamp}.xlsx"),
            "pdf" => (PdfReportRenderer.Render(table), "application/pdf", $"weighments-{stamp}.pdf"),
            _ => throw new ArgumentException("format must be csv, xlsx or pdf."),
        };
    }

    private Task<List<CloudTransactionRow>> CompletedAsync(CloudPrincipal user, DateTimeOffset from, DateTimeOffset to, string? site, CancellationToken ct) => db.ReadAsync(async s =>
    {
        var (where, args) = await FilterAsync(s, user, new TransactionQuery { Site = site, Status = nameof(TransactionStatus.Completed) }, ct).ConfigureAwait(false);
        args.Add("cfrom", Iso.ToText(from));
        args.Add("cto", Iso.ToText(to));
        return (await s.QueryAsync<CloudTransactionRow>(
            $"SELECT {TxColumns} FROM cloud_transactions t JOIN sites s ON s.id = t.site_id WHERE {where} AND t.completed_utc >= @cfrom AND t.completed_utc < @cto LIMIT 200000",
            args, ct).ConfigureAwait(false)).ToList();
    }, ct);

    // ------------------------------------------------------------------ notifications

    public Task<List<NotificationDto>> NotificationsAsync(CloudPrincipal user, long? afterId, bool unreadOnly, int limit, CancellationToken ct = default) => db.ReadAsync(async s =>
    {
        var where = new List<string> { "1 = 1" };
        if (user.SiteIds is not null) where.Add(s.Dialect.InList("n.site_id", "@sites"));
        if (afterId is not null) where.Add("n.id > @afterId");
        if (unreadOnly) where.Add("r.user_id IS NULL");
        var rows = await s.QueryAsync<NotificationRow>($"""
            SELECT n.id, s.code AS site_code, n.at_utc, n.severity, n.category, n.title, n.message, n.entity_id,
                   CASE WHEN r.user_id IS NULL THEN 0 ELSE 1 END AS is_read
            FROM cloud_notifications n JOIN sites s ON s.id = n.site_id
            LEFT JOIN notification_reads r ON r.notification_id = n.id AND r.user_id = @UserId
            WHERE {string.Join(" AND ", where)} ORDER BY n.id DESC LIMIT @limit
            """, new { user.UserId, sites = user.SiteIds?.DefaultIfEmpty(-1).ToArray(), afterId, limit = Math.Clamp(limit, 1, 500) }, ct).ConfigureAwait(false);
        return rows.Select(n => new NotificationDto(n.Id, n.SiteCode, n.AtUtc, n.Severity, n.Category, n.Title, n.Message, n.EntityId, n.IsRead)).ToList();
    }, ct);

    public Task<int> MarkReadAsync(CloudPrincipal user, IReadOnlyList<long>? ids, CancellationToken ct = default) => db.InTransactionAsync(async s =>
    {
        var visible = (await s.QueryAsync<long>($"""
            SELECT n.id FROM cloud_notifications n
            LEFT JOIN notification_reads r ON r.notification_id = n.id AND r.user_id = @UserId
            WHERE r.user_id IS NULL{(user.SiteIds is null ? string.Empty : " AND " + s.Dialect.InList("n.site_id", "@sites"))}{(ids is null ? string.Empty : " AND " + s.Dialect.InList("n.id", "@ids"))}
            """, new { user.UserId, sites = user.SiteIds?.DefaultIfEmpty(-1).ToArray(), ids = ids?.Take(1000).DefaultIfEmpty(-1).ToArray() }, ct).ConfigureAwait(false)).ToList();
        foreach (var id in visible)
        {
            await s.ExecuteAsync("INSERT INTO notification_reads (user_id, notification_id) VALUES (@UserId, @id)", new { user.UserId, id }, ct).ConfigureAwait(false);
        }
        return visible.Count;
    }, ct);

    // ------------------------------------------------------------------ audit

    public async Task<PagedDto<AuditEventDto>> AuditAsync(CloudPrincipal user, string? site, DateTimeOffset? from, DateTimeOffset? to, string? q, string? category, int page, int pageSize,
        CancellationToken ct = default)
    {
        if (!user.Permissions.Contains(Permissions.AuditView)) throw new UnauthorizedAccessException("You do not have the permission to view the audit trail.");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        return await db.ReadAsync(async s =>
        {
            var where = new List<string> { "1 = 1" };
            var args = new Dapper.DynamicParameters();
            if (user.SiteIds is not null) { where.Add(s.Dialect.InList("a.site_id", "@sites")); args.Add("sites", user.SiteIds.DefaultIfEmpty(-1).ToArray()); }
            if (!string.IsNullOrWhiteSpace(site)) { where.Add("lower(s.code) = lower(@site)"); args.Add("site", site.Trim()); }
            if (from is { } f) { where.Add("a.at_utc >= @from"); args.Add("from", Iso.ToText(f)); }
            if (to is { } t) { where.Add("a.at_utc < @to"); args.Add("to", Iso.ToText(t)); }
            if (!string.IsNullOrWhiteSpace(category)) { where.Add("a.category = @category"); args.Add("category", category.Trim()); }
            if (!string.IsNullOrWhiteSpace(q))
            {
                where.Add($"({s.Dialect.ContainsCi("a.summary", "@q")} OR {s.Dialect.ContainsCi("a.username", "@q")} OR {s.Dialect.ContainsCi("a.action", "@q")} OR {s.Dialect.ContainsCi("a.entity_id", "@q")})");
                args.Add("q", SqlDialect.LikePattern(q.Trim()));
            }
            var sql = string.Join(" AND ", where);
            var total = await s.QuerySingleAsync<long>($"SELECT COUNT(*) FROM cloud_audit_events a JOIN sites s ON s.id = a.site_id WHERE {sql}", args, ct).ConfigureAwait(false);
            args.Add("limit", pageSize);
            args.Add("offset", (page - 1) * pageSize);
            var rows = await s.QueryAsync<AuditRow>($"""
                SELECT a.id, s.code AS site_code, a.local_id, a.at_utc, a.username, a.category, a.action, a.entity_type, a.entity_id, a.summary, a.reason, a.prev_hash, a.hash
                FROM cloud_audit_events a JOIN sites s ON s.id = a.site_id WHERE {sql} ORDER BY a.at_utc DESC, a.id DESC LIMIT @limit OFFSET @offset
                """, args, ct).ConfigureAwait(false);
            return new PagedDto<AuditEventDto>(rows.Select(a => new AuditEventDto(a.Id, a.SiteCode, a.AtUtc, a.Username, a.Category, a.Action, a.EntityType, a.EntityId, a.Summary, a.Reason)).ToList(),
                total, page, pageSize);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Checks that the uploaded audit events of a site form one unbroken hash chain.</summary>
    public async Task<AuditContinuityDto> VerifyAuditAsync(CloudPrincipal user, string siteCode, CancellationToken ct = default)
    {
        if (!user.Permissions.Contains(Permissions.AuditView)) throw new UnauthorizedAccessException("You do not have the permission to view the audit trail.");
        return await db.ReadAsync(async s =>
        {
            var site = await CloudStore.SiteByCodeAsync(s, siteCode, ct).ConfigureAwait(false);
            if (site is null || !user.CanAccess(site.Id)) throw new KeyNotFoundException($"Site '{siteCode}' not found.");
            long count = 0;
            long? first = null, last = null;
            string? previous = null;
            var cursor = long.MinValue;
            while (true)
            {
                var batch = (await s.QueryAsync<(long LocalId, string PrevHash, string Hash)>(
                    "SELECT local_id, prev_hash, hash FROM cloud_audit_events WHERE site_id = @Id AND local_id > @cursor ORDER BY local_id LIMIT 5000",
                    new { site.Id, cursor }, ct).ConfigureAwait(false)).ToList();
                if (batch.Count == 0) break;
                foreach (var e in batch)
                {
                    first ??= e.LocalId;
                    if (previous is not null && e.PrevHash != previous)
                    {
                        return new AuditContinuityDto(site.Code, count, false, first, last, e.LocalId,
                            $"The chain is broken at site event {e.LocalId}: it does not follow event {last}. Events are missing or were altered before upload.");
                    }
                    previous = e.Hash;
                    last = e.LocalId;
                    count++;
                }
                cursor = batch[^1].LocalId;
            }
            if (count == 0) return new AuditContinuityDto(site.Code, 0, true, null, null, null, "No audit events have been uploaded by this site yet.");
            var fromStart = await s.QuerySingleOrDefaultAsync<string>("SELECT prev_hash FROM cloud_audit_events WHERE site_id = @Id AND local_id = @first", new { site.Id, first }, ct).ConfigureAwait(false)
                == SyncIngestService.Genesis;
            return new AuditContinuityDto(site.Code, count, true, first, last, null,
                $"{count} events ({first}–{last}) form an unbroken chain{(fromStart ? " from the first event of the site" : string.Empty)}.");
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ public ticket check

    /// <summary>
    /// Public check of a ticket QR code against the synchronised record. The cloud does not hold the site's signing key, so
    /// it confirms the data matches its copy; the signature itself is verified at the site (Tools → Verify ticket).
    /// </summary>
    public Task<TicketVerifyDto> VerifyTicketAsync(string? ticketNo, string? uidText, string? netText, CancellationToken ct = default) => db.ReadAsync(async s =>
    {
        if (string.IsNullOrWhiteSpace(ticketNo) || !Guid.TryParse(uidText, out var uid) || !long.TryParse(netText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var net))
        {
            return new TicketVerifyDto(false, "This is not a valid AWMS ticket code.", null, null, null, null, null, null);
        }
        var row = await s.QuerySingleOrDefaultAsync<CloudTransactionRow>($"SELECT {TxColumns} FROM cloud_transactions t JOIN sites s ON s.id = t.site_id WHERE t.uid = @uid",
            new { uid = uid.ToString("D") }, ct).ConfigureAwait(false);
        if (row is null) return new TicketVerifyDto(false, "No synchronised transaction matches this ticket. It may not have been uploaded yet, or the ticket is not genuine.", ticketNo, null, null, null, null, null);
        var first = JsonSerializer.Deserialize<SyncTransactionDto>(row.PayloadJson, Api.Json)!.FirstG;
        if (!string.Equals(row.TicketNo, ticketNo, StringComparison.Ordinal) || (row.NetG ?? first ?? 0) != net)
        {
            return new TicketVerifyDto(false, "The ticket does not match the synchronised record (number or weight differ).", ticketNo, row.SiteCode, null, null, null, null);
        }
        if (row.Status == nameof(TransactionStatus.Cancelled))
        {
            return new TicketVerifyDto(false, $"Ticket {row.TicketNo} was cancelled.", row.TicketNo, row.SiteCode, row.Status, row.VehicleReg, row.NetG, row.CompletedUtc);
        }
        return new TicketVerifyDto(true, $"Ticket {row.TicketNo} matches the record synchronised from site {row.SiteCode}.", row.TicketNo, row.SiteCode, row.Status, row.VehicleReg, row.NetG, row.CompletedUtc);
    }, ct);

    private static string SafeFileName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return sb.ToString();
    }
}
