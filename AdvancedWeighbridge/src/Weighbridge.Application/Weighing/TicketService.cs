using System.Globalization;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Application.Weighing;

/// <summary>Physical printing is platform specific (WPF/Windows spooler); the application layer only calls this.</summary>
public interface IPrintService
{
    Task PrintAsync(TicketData data, TicketTemplate template, int copies, string? printerName, bool escPosRaw, CancellationToken ct);
}

public sealed record PrintOutcome(int PrintNumber, bool IsReprint, string? PdfPath);

public sealed record TicketCheck(bool Valid, string Message, TransactionEntity? Transaction);

/// <summary>Ticket data, PDF, printing with history, and QR verification.</summary>
public sealed class TicketService(Database db, SettingsService settings, AuditService audit, AppPaths paths, TimeProvider time)
{
    public async Task<TicketTemplate> TemplateAsync(CancellationToken ct = default)
    {
        var printing = await settings.GetAsync<PrintingSettings>(SettingKeys.Printing, ct).ConfigureAwait(false);
        var loc = await settings.GetAsync<LocalizationSettings>(SettingKeys.Localization, ct).ConfigureAwait(false);
        return printing.Template with { Display = loc.Display, TimeZoneId = loc.TimeZoneId ?? printing.Template.TimeZoneId };
    }

    public async Task<TicketData> BuildAsync(long transactionId, int reprintNumber = 0, CancellationToken ct = default)
    {
        var company = await settings.GetAsync<CompanySettings>(SettingKeys.Company, ct).ConfigureAwait(false);
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var template = await TemplateAsync(ct).ConfigureAwait(false);
        var key = await settings.TicketSigningKeyAsync(ct).ConfigureAwait(false);
        return await db.ReadAsync(async s =>
        {
            var t = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            var ms = await TransactionStore.MeasurementsAsync(s, t.Id, ct).ConfigureAwait(false);
            var evidence = template.ShowImages ? await TransactionStore.EvidenceAsync(s, t.Id, ct).ConfigureAwait(false) : [];
            var site = await SiteStore.GetSiteAsync(s, t.SiteId, ct).ConfigureAwait(false);
            var wb = await SiteStore.GetWeighbridgeAsync(s, t.WeighbridgeId, ct).ConfigureAwait(false);
            var order = t.OrderId is { } o ? await MasterDataStore.OrderAsync(s, o, ct).ConfigureAwait(false) : null;
            var operatorName = t.CompletedBy is { } cb ? (await UserStore.GetAsync(s, cb, ct).ConfigureAwait(false))?.DisplayName : t.CreatedByName;

            TicketWeighing? W(long? measurementId)
            {
                var m = ms.FirstOrDefault(x => x.Id == measurementId);
                return m is null ? null : new TicketWeighing(m.Weight, m.CapturedUtc, SourceLabel(m), m.Source == MeasurementSource.Manual, m.IsStable);
            }

            var images = new List<byte[]>();
            foreach (var e in evidence.Take(3))
            {
                var file = Path.Combine(paths.EvidenceDirectory, e.FilePath);
                if (File.Exists(file)) images.Add(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false));
            }

            return new TicketData
            {
                Company = new CompanyInfo
                {
                    Name = company.Name, Address = company.Address, Phone = company.Phone, Email = company.Email, TaxNo = company.TaxNo,
                    Logo = string.IsNullOrEmpty(company.LogoBase64) ? null : Convert.FromBase64String(company.LogoBase64),
                },
                TicketNo = t.TicketNo,
                TransactionUid = t.Uid,
                Status = t.Status switch { TransactionStatus.Pending => TicketStatus.Pending, TransactionStatus.Cancelled => TicketStatus.Cancelled, _ => TicketStatus.Completed },
                SiteName = site?.Name,
                WeighbridgeName = wb?.Name,
                VehicleReg = t.VehicleReg,
                DriverName = t.DriverName,
                Customer = t.CustomerName,
                Supplier = t.SupplierName,
                Material = t.MaterialName,
                OrderNo = order?.OrderNo,
                DeliveryNote = t.DeliveryNoteNo,
                Reference = t.ReferenceNo,
                Direction = t.Direction.ToString(),
                Remarks = t.Remarks,
                First = W(t.FirstMeasurementId),
                Second = W(t.SecondMeasurementId),
                Gross = t.Gross,
                Tare = t.Tare,
                Net = t.Net,
                TareMethodLabel = t.TareMethod switch { TareMethod.StoredTare => "stored tare", TareMethod.ManualTare => "manual tare", TareMethod.Measured => "measured", _ => null },
                OperatorName = operatorName,
                ModeLabel = t.OperatingMode switch { OperatingMode.Automatic => "Automatic", OperatingMode.Manual => "Manual", _ => "Semi-automatic" },
                HasManualWeight = t.HasManualWeight,
                HasOverride = t.HasOverride,
                CancelReason = t.CancelReason,
                ReprintNumber = reprintNumber,
                PrintedAt = time.GetUtcNow(),
                VerificationPayload = TicketVerification.CreatePayload(key, t.TicketNo, t.Uid, t.NetG ?? t.FirstG ?? 0, cloud.VerifyBaseUrl),
                Images = images,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> PdfAsync(long transactionId, CancellationToken ct = default) =>
        TicketRenderer.RenderPdf(await BuildAsync(transactionId, 0, ct).ConfigureAwait(false), await TemplateAsync(ct).ConfigureAwait(false));

    /// <summary>Prints (or re-prints) a ticket, records the print history, and keeps a PDF copy.</summary>
    public async Task<PrintOutcome> PrintAsync(UserContext user, long transactionId, IPrintService printer, string? reason = null, int? copies = null, CancellationToken ct = default)
    {
        user.Demand(Permissions.TicketPrint);
        var printing = await settings.GetAsync<PrintingSettings>(SettingKeys.Printing, ct).ConfigureAwait(false);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        var previous = await db.ReadAsync(s => TransactionStore.PrintCountAsync(s, transactionId, ct), ct).ConfigureAwait(false);
        var isReprint = previous > 0;
        if (isReprint)
        {
            user.Demand(Permissions.TicketReprint);
            if (approvals.ReprintRequiresReason && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to reprint a ticket.");
        }
        var template = await TemplateAsync(ct).ConfigureAwait(false);
        var data = await BuildAsync(transactionId, isReprint ? previous : 0, ct).ConfigureAwait(false);
        var n = copies ?? Math.Max(1, template.Copies);
        await printer.PrintAsync(data, template, n, printing.PrinterName, printing.UseEscPosRaw, ct).ConfigureAwait(false);

        string? pdfPath = null;
        if (printing.SavePdfCopy)
        {
            var folder = Path.Combine(paths.ExportDirectory, "tickets", data.PrintedAt.ToString("yyyy", CultureInfo.InvariantCulture), data.PrintedAt.ToString("MM", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            pdfPath = Path.Combine(folder, $"{Safe(data.TicketNo)}{(isReprint ? $"-reprint{previous}" : string.Empty)}.pdf");
            await File.WriteAllBytesAsync(pdfPath, TicketRenderer.RenderPdf(data, template), ct).ConfigureAwait(false);
        }
        await RecordAsync(user, transactionId, isReprint ? "Reprint" : "Print", n, printing.PrinterName, reason, ct).ConfigureAwait(false);
        return new PrintOutcome(previous + 1, isReprint, pdfPath);
    }

    public async Task<string> ExportPdfAsync(UserContext user, long transactionId, string path, CancellationToken ct = default)
    {
        user.Demand(Permissions.HistoryView);
        await File.WriteAllBytesAsync(path, await PdfAsync(transactionId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await RecordAsync(user, transactionId, "Pdf", 1, null, null, ct).ConfigureAwait(false);
        return path;
    }

    private Task RecordAsync(UserContext user, long transactionId, string action, int copies, string? printer, string? reason, CancellationToken ct) =>
        db.InTransactionAsync(async s =>
        {
            var t = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            await TransactionStore.AddPrintAsync(s, new PrintHistoryEntity
            {
                TransactionId = transactionId, Action = action, Copies = copies, Printer = printer, Reason = reason, UserId = user.UserId, AtUtc = time.GetUtcNow(),
            }, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Ticket", action, $"Ticket {t.TicketNo}: {action.ToLowerInvariant()} ({copies} cop{(copies == 1 ? "y" : "ies")})", "Transaction", t.Uid.ToString("D"), reason: reason, ct: ct).ConfigureAwait(false);
            return 0;
        }, ct);

    /// <summary>Checks a scanned ticket QR code: signature valid and the weight matches the database.</summary>
    public async Task<TicketCheck> VerifyAsync(string payload, CancellationToken ct = default)
    {
        var parsed = TicketVerification.Parse(payload);
        if (parsed is null) return new TicketCheck(false, "This is not an AWMS ticket code.", null);
        var key = await settings.TicketSigningKeyAsync(ct).ConfigureAwait(false);
        if (!TicketVerification.Verify(key, parsed)) return new TicketCheck(false, "The signature is not valid: the ticket was not issued by this installation or was altered.", null);
        var t = await db.ReadAsync(s => TransactionStore.GetByUidAsync(s, parsed.Uid, ct), ct).ConfigureAwait(false);
        if (t is null) return new TicketCheck(false, "The signature is valid but the transaction is not in this database.", null);
        if (t.TicketNo != parsed.TicketNo || (t.NetG ?? t.FirstG ?? 0) != parsed.NetGrams) return new TicketCheck(false, "The ticket does not match the stored transaction.", t);
        if (t.Status == TransactionStatus.Cancelled) return new TicketCheck(false, $"Ticket {t.TicketNo} was cancelled: {t.CancelReason}", t);
        return new TicketCheck(true, $"Ticket {t.TicketNo} is genuine. Net {WeightDisplayOptions.DefaultKg.Format(Weight.FromGrams(t.NetG ?? t.FirstG ?? 0))}.", t);
    }

    public static string SourceLabel(MeasurementEntity m) => m.Source switch
    {
        // Weights from the built-in simulator are never presented as real measurements.
        MeasurementSource.Indicator when string.Equals(m.PortDescription, "Simulator", StringComparison.Ordinal) => "SIMULATOR – training, not a real weighing",
        MeasurementSource.Manual => m.CorrectsMeasurementId is null ? "Manual entry" : "Manual correction",
        MeasurementSource.StoredTare => "Stored tare",
        _ => m.UnstableOverride ? "Indicator, override" : m.IsStable ? "Indicator, stable" : "Indicator",
    };

    private static string Safe(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '-' : c).ToArray());
}
