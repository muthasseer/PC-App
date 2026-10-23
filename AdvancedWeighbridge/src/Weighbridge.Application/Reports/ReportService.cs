using System.Globalization;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Reporting.Reports;

namespace Weighbridge.Application.Reports;

public enum ReportKind
{
    Transactions,
    DailySummary,
    WeeklySummary,
    MonthlySummary,
    AnnualSummary,
    VehicleWise,
    MaterialWise,
    CustomerWise,
    SupplierWise,
    OperatorWise,
    ManualOverride,
    Cancelled,
    CommunicationErrors,
    AuditTrail,
}

public enum ExportFormat { Xlsx, Csv, Pdf }

public sealed record ReportRequest(ReportKind Kind, TransactionFilter Filter);

public sealed record ExportedReport(byte[] Content, string FileName, string ContentType, int Rows);

/// <summary>Builds every report from the same filtered data and exports it. Exports are audited.</summary>
public sealed class ReportService(Database db, SettingsService settings, AuditService audit, TimeProvider time)
{
    public static string Title(ReportKind k) => k switch
    {
        ReportKind.Transactions => "Weighment transactions",
        ReportKind.DailySummary => "Daily summary",
        ReportKind.WeeklySummary => "Weekly summary",
        ReportKind.MonthlySummary => "Monthly summary",
        ReportKind.AnnualSummary => "Annual summary",
        ReportKind.VehicleWise => "Vehicle-wise report",
        ReportKind.MaterialWise => "Material-wise report",
        ReportKind.CustomerWise => "Customer-wise report",
        ReportKind.SupplierWise => "Supplier-wise report",
        ReportKind.OperatorWise => "Operator report",
        ReportKind.ManualOverride => "Manual entries and overrides",
        ReportKind.Cancelled => "Cancelled transactions",
        ReportKind.CommunicationErrors => "Indicator communication errors",
        _ => "Audit trail",
    };

    public async Task<ReportTable> BuildAsync(UserContext user, ReportRequest request, CancellationToken ct = default)
    {
        user.Demand(Permissions.ReportsView);
        if (request.Kind == ReportKind.AuditTrail) user.Demand(Permissions.AuditView);
        var company = await settings.GetAsync<CompanySettings>(SettingKeys.Company, ct).ConfigureAwait(false);
        var loc = await settings.GetAsync<LocalizationSettings>(SettingKeys.Localization, ct).ConfigureAwait(false);
        var zone = Zone(loc.TimeZoneId);
        var f = request.Filter;
        var subtitle = TransactionStore.Describe(f);

        var table = request.Kind switch
        {
            ReportKind.CommunicationErrors => await CommunicationErrorsAsync(f, ct).ConfigureAwait(false),
            ReportKind.AuditTrail => await AuditTrailAsync(f, ct).ConfigureAwait(false),
            _ => await TransactionReportAsync(request.Kind, f, zone, ct).ConfigureAwait(false),
        };
        return table with
        {
            Title = Title(request.Kind),
            Subtitle = subtitle,
            CompanyName = company.Name,
            GeneratedAt = time.GetUtcNow(),
            GeneratedBy = user.DisplayName,
            Display = loc.Display,
            DateFormat = $"{loc.DateFormat} HH:mm",
            TimeZone = zone,
        };
    }

    public async Task<ExportedReport> ExportAsync(UserContext user, ReportRequest request, ExportFormat format, CancellationToken ct = default)
    {
        user.Demand(Permissions.ReportsExport);
        var table = await BuildAsync(user, request, ct).ConfigureAwait(false);
        var (content, ext, type) = format switch
        {
            ExportFormat.Csv => (CsvExporter.ToBytes(table), "csv", "text/csv"),
            ExportFormat.Pdf => (PdfReportRenderer.Render(table), "pdf", "application/pdf"),
            _ => (XlsxExporter.ToBytes(table), "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        };
        var name = $"{request.Kind}-{time.GetUtcNow():yyyyMMdd-HHmmss}.{ext}";
        await audit.WriteAsync(user, "Reports", "Exported", $"{table.Title} exported as {format} ({table.Rows.Count} rows)", reason: table.Subtitle, ct: ct).ConfigureAwait(false);
        return new ExportedReport(content, name, type, table.Rows.Count);
    }

    private async Task<ReportTable> TransactionReportAsync(ReportKind kind, TransactionFilter f, TimeZoneInfo zone, CancellationToken ct)
    {
        // Summaries count completed transactions unless the user chose a status.
        var filter = kind switch
        {
            ReportKind.Cancelled => f with { Status = TransactionStatus.Cancelled },
            ReportKind.Transactions or ReportKind.ManualOverride => f,
            _ => f.Status is null ? f with { Status = TransactionStatus.Completed } : f,
        };
        var rows = new List<TransactionEntity>();
        await using (var s = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            await foreach (var t in TransactionStore.StreamAsync(s, filter with { Sort = TransactionSort.Created, Descending = false }, ct: ct).ConfigureAwait(false))
            {
                if (kind == ReportKind.ManualOverride && !t.HasManualWeight && !t.HasOverride) continue;
                rows.Add(t);
            }
        }

        static Weight W(long? g) => Weight.FromGrams(g ?? 0);

        switch (kind)
        {
            case ReportKind.Transactions:
            case ReportKind.ManualOverride:
            case ReportKind.Cancelled:
            {
                var cols = new List<ReportColumn>
                {
                    new("Ticket"), new("Status", Width: 0.8f), new("First weighing", ColumnKind.DateTime, 1.2f), new("Completed", ColumnKind.DateTime, 1.2f),
                    new("Vehicle"), new("Customer", Width: 1.3f), new("Supplier", Width: 1.3f), new("Material", Width: 1.3f),
                    new("First", ColumnKind.Weight), new("Second", ColumnKind.Weight), new("Gross", ColumnKind.Weight), new("Tare", ColumnKind.Weight), new("Net", ColumnKind.Weight),
                    new("Mode", Width: 0.9f), new("Manual", Width: 0.6f), new("Operator"),
                };
                if (kind == ReportKind.Cancelled) cols.Add(new ReportColumn("Cancel reason", Width: 1.6f));
                var data = rows.Select(t =>
                {
                    var r = new List<object?>
                    {
                        t.TicketNo, t.Status.ToString(), t.CreatedUtc, t.CompletedUtc, t.VehicleReg, t.CustomerName, t.SupplierName, t.MaterialName,
                        t.First, t.Second, t.Gross, t.Tare, t.Net, t.OperatingMode.ToString(), t.HasManualWeight || t.HasOverride, t.CreatedByName,
                    };
                    if (kind == ReportKind.Cancelled) r.Add(t.CancelReason);
                    return (IReadOnlyList<object?>)r;
                }).ToList();
                var completed = rows.Where(t => t.Status == TransactionStatus.Completed).ToList();
                var totals = new List<object?> { $"Total ({rows.Count})", null, null, null, null, null, null, null, null, null, W(completed.Sum(t => t.GrossG)), W(completed.Sum(t => t.TareG)), W(completed.Sum(t => t.NetG)), null, null, null };
                if (kind == ReportKind.Cancelled) totals.Add(null);
                return new ReportTable { Title = string.Empty, Columns = cols, Rows = data, Totals = totals, Landscape = true };
            }

            case ReportKind.DailySummary:
            case ReportKind.WeeklySummary:
            case ReportKind.MonthlySummary:
            case ReportKind.AnnualSummary:
            {
                string Key(TransactionEntity t)
                {
                    var local = TimeZoneInfo.ConvertTime(t.CompletedUtc ?? t.CreatedUtc, zone).DateTime;
                    return kind switch
                    {
                        ReportKind.DailySummary => local.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
                        ReportKind.WeeklySummary => $"{ISOWeek.GetYear(local)}-W{ISOWeek.GetWeekOfYear(local):00}",
                        ReportKind.MonthlySummary => local.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                        _ => local.ToString("yyyy", CultureInfo.InvariantCulture),
                    };
                }
                return Grouped(rows, Key, kind switch { ReportKind.DailySummary => "Date", ReportKind.WeeklySummary => "Week", ReportKind.MonthlySummary => "Month", _ => "Year" }, sortByKey: true);
            }

            case ReportKind.VehicleWise:
                return Grouped(rows, t => t.VehicleReg, "Vehicle");
            case ReportKind.MaterialWise:
                return Grouped(rows, t => t.MaterialName ?? "(none)", "Material");
            case ReportKind.CustomerWise:
                return Grouped(rows, t => t.CustomerName ?? "(none)", "Customer");
            case ReportKind.SupplierWise:
                return Grouped(rows, t => t.SupplierName ?? "(none)", "Supplier");
            default:
                return Grouped(rows, t => t.CreatedByName ?? t.CreatedBy.ToString(CultureInfo.InvariantCulture), "Operator");
        }
    }

    private static ReportTable Grouped(List<TransactionEntity> rows, Func<TransactionEntity, string> key, string keyHeader, bool sortByKey = false)
    {
        var groups = rows.GroupBy(key).Select(g => new
        {
            Key = g.Key,
            Count = (long)g.Count(),
            Gross = g.Sum(t => t.GrossG ?? 0),
            Tare = g.Sum(t => t.TareG ?? 0),
            Net = g.Sum(t => t.NetG ?? 0),
            Manual = (long)g.Count(t => t.HasManualWeight || t.HasOverride),
        });
        groups = sortByKey ? groups.OrderBy(g => g.Key, StringComparer.Ordinal) : groups.OrderByDescending(g => g.Net).ThenBy(g => g.Key, StringComparer.Ordinal);
        var list = groups.ToList();
        return new ReportTable
        {
            Title = string.Empty,
            Columns = [new(keyHeader, Width: 1.6f), new("Transactions", ColumnKind.Integer), new("Gross", ColumnKind.Weight), new("Tare", ColumnKind.Weight), new("Net", ColumnKind.Weight), new("Average net", ColumnKind.Weight), new("Manual / override", ColumnKind.Integer)],
            Rows = list.Select(g => (IReadOnlyList<object?>)new object?[]
            {
                g.Key, g.Count, Weight.FromGrams(g.Gross), Weight.FromGrams(g.Tare), Weight.FromGrams(g.Net), Weight.FromGrams(g.Count == 0 ? 0 : g.Net / g.Count), g.Manual,
            }).ToList(),
            Totals = ["Total", list.Sum(g => g.Count), Weight.FromGrams(list.Sum(g => g.Gross)), Weight.FromGrams(list.Sum(g => g.Tare)), Weight.FromGrams(list.Sum(g => g.Net)), null, list.Sum(g => g.Manual)],
        };
    }

    private async Task<ReportTable> CommunicationErrorsAsync(TransactionFilter f, CancellationToken ct)
    {
        var events = await db.ReadAsync(s => DeviceEventStore.QueryAsync(s, f.From ?? DateTimeOffset.UnixEpoch, f.To ?? time.GetUtcNow().AddDays(1), null, 100_000, ct), ct).ConfigureAwait(false);
        return new ReportTable
        {
            Title = string.Empty,
            Columns = [new("Time", ColumnKind.DateTime, 1.2f), new("Device"), new("Event"), new("Message", Width: 3)],
            Rows = events.Select(e => (IReadOnlyList<object?>)new object?[] { e.AtUtc, e.Device, e.EventType, e.Message }).ToList(),
            Totals = [$"{events.Count} events", null, null, null],
        };
    }

    private async Task<ReportTable> AuditTrailAsync(TransactionFilter f, CancellationToken ct)
    {
        var page = await db.ReadAsync(s => AuditStore.QueryAsync(s, new AuditFilter { From = f.From, To = f.To, Text = f.Text, PageSize = 10_000 }, ct), ct).ConfigureAwait(false);
        return new ReportTable
        {
            Title = string.Empty,
            Columns = [new("#", ColumnKind.Integer, 0.5f), new("Time", ColumnKind.DateTime, 1.2f), new("User"), new("Category"), new("Action"), new("Summary", Width: 3), new("Reason", Width: 1.5f), new("Hash", Width: 1.2f)],
            Rows = page.Items.Select(e => (IReadOnlyList<object?>)new object?[] { e.Id, e.AtUtc, e.Username, e.Category, e.Action, e.Summary, e.Reason, e.Hash[..12] }).ToList(),
            Landscape = true,
        };
    }

    private static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}
