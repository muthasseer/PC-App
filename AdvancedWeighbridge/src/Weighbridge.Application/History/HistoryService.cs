using System.Globalization;
using System.Security.Cryptography;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.History;

public sealed record TimelineEntry(DateTimeOffset At, string Kind, string Title, string Detail, string? User);

public sealed record TransactionDetail(
    TransactionEntity Transaction,
    IReadOnlyList<MeasurementEntity> Measurements,
    IReadOnlyDictionary<long, IReadOnlyList<RawFrameEntity>> RawFrames,
    IReadOnlyList<EvidenceEntity> Evidence,
    IReadOnlyList<TransactionChangeEntity> Changes,
    IReadOnlyList<PrintHistoryEntity> Prints,
    IReadOnlyList<ApprovalEntity> Approvals,
    IReadOnlyList<AuditEventEntity> Audit,
    TransactionEntity? Replaces,
    TransactionEntity? ReplacedBy,
    IReadOnlyList<TimelineEntry> Timeline);

public sealed record EvidenceFile(EvidenceEntity Evidence, byte[]? Data, bool IntegrityOk, string Message);

/// <summary>History search and the full, traceable story of one transaction.</summary>
public sealed class HistoryService(Database db, AppPaths paths)
{
    public Task<PagedResult<TransactionEntity>> SearchAsync(UserContext user, TransactionFilter filter, CancellationToken ct = default)
    {
        user.Demand(Permissions.HistoryView);
        return db.ReadAsync(s => TransactionStore.SearchAsync(s, filter, ct), ct);
    }

    public Task<IReadOnlyList<TransactionEntity>> RecentAsync(int count = 20, CancellationToken ct = default) => db.ReadAsync(s => TransactionStore.RecentAsync(s, count, null, ct), ct);

    public Task<IReadOnlyList<TransactionEntity>> PendingAsync(CancellationToken ct = default) => db.ReadAsync(s => TransactionStore.PendingAsync(s, null, ct), ct);

    public async Task<TransactionDetail> DetailAsync(UserContext user, long id, CancellationToken ct = default)
    {
        user.Demand(Permissions.HistoryView);
        return await db.ReadAsync(async s =>
        {
            var t = await TransactionStore.GetAsync(s, id, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            var measurements = await TransactionStore.MeasurementsAsync(s, id, ct).ConfigureAwait(false);
            var frames = new Dictionary<long, IReadOnlyList<RawFrameEntity>>();
            foreach (var m in measurements) frames[m.Id] = await TransactionStore.RawFramesAsync(s, m.Id, ct).ConfigureAwait(false);
            var evidence = await TransactionStore.EvidenceAsync(s, id, ct).ConfigureAwait(false);
            var changes = await TransactionStore.ChangesAsync(s, id, ct).ConfigureAwait(false);
            var prints = await TransactionStore.PrintsAsync(s, id, ct).ConfigureAwait(false);
            var approvals = await ApprovalStore.ForEntityAsync(s, "Transaction", t.Uid.ToString("D"), ct).ConfigureAwait(false);
            var auditEvents = user.Has(Permissions.AuditView) ? await AuditStore.ForEntityAsync(s, "Transaction", t.Uid.ToString("D"), ct).ConfigureAwait(false) : [];
            var replaces = t.ReplacesTransactionId is { } r ? await TransactionStore.GetAsync(s, r, ct).ConfigureAwait(false) : null;
            var replacedBy = await TransactionStore.ReplacementOfAsync(s, id, ct).ConfigureAwait(false);

            var timeline = new List<TimelineEntry>
            {
                new(t.CreatedUtc, "Created", $"Ticket {t.TicketNo} opened", $"{t.VehicleReg}, {t.OperatingMode}", t.CreatedByName),
            };
            foreach (var m in measurements)
            {
                var label = m.Stage == 1 ? "First weight" : "Second weight";
                var detail = $"{WeightDisplayOptions.DefaultKg.Format(m.Weight)} – {Weighing.TicketService.SourceLabel(m)}";
                if (m.ReadingAgeMs is { } age) detail += $", reading age {age} ms";
                if (m.ProfileName is not null) detail += $", profile {m.ProfileName} v{m.ProfileVersion}";
                if (m.ManualReason is not null) detail += $", reason: {m.ManualReason}";
                if (m.CorrectionReason is not null) detail += $", correction: {m.CorrectionReason}";
                timeline.Add(new TimelineEntry(m.CapturedUtc, m.CorrectsMeasurementId is null ? "Measurement" : "Correction", label, detail, m.OperatorName));
            }
            timeline.AddRange(changes.Select(c => new TimelineEntry(c.AtUtc, "Change", $"{c.Field} changed", $"'{c.OldValue}' → '{c.NewValue}'" + (c.Reason is null ? string.Empty : $" ({c.Reason})"), c.UserName)));
            timeline.AddRange(prints.Select(p => new TimelineEntry(p.AtUtc, "Print", p.Action, $"{p.Copies} cop{(p.Copies == 1 ? "y" : "ies")}{(p.Printer is null ? string.Empty : " on " + p.Printer)}{(p.Reason is null ? string.Empty : $" – {p.Reason}")}", p.UserName)));
            timeline.AddRange(approvals.Select(a => new TimelineEntry(a.DecidedUtc ?? a.RequestedUtc, "Approval", $"{a.RequestType} {a.Status.ToLowerInvariant()}", a.DecisionReason ?? string.Empty, a.DecidedByName ?? a.RequestedByName)));
            if (t.CompletedUtc is { } done) timeline.Add(new TimelineEntry(done, "Completed", "Completed", $"Net {WeightDisplayOptions.DefaultKg.Format(Weight.FromGrams(t.NetG ?? 0))} ({t.TareMethod})", null));
            if (t.CancelledUtc is { } cancelled) timeline.Add(new TimelineEntry(cancelled, "Cancelled", "Cancelled", t.CancelReason ?? string.Empty, null));
            if (replacedBy is not null) timeline.Add(new TimelineEntry(replacedBy.CreatedUtc, "Reissued", $"Replaced by ticket {replacedBy.TicketNo}", string.Empty, replacedBy.CreatedByName));

            return new TransactionDetail(t, measurements, frames, evidence, changes, prints, approvals, auditEvents, replaces, replacedBy,
                timeline.OrderBy(e => e.At).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList());
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Reads an evidence image and checks it against the SHA-256 recorded at capture time.</summary>
    public async Task<EvidenceFile> ReadEvidenceAsync(EvidenceEntity e, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(Path.Combine(paths.EvidenceDirectory, e.FilePath));
        if (!path.StartsWith(Path.GetFullPath(paths.EvidenceDirectory), StringComparison.Ordinal)) return new EvidenceFile(e, null, false, "Invalid evidence path.");
        if (!File.Exists(path)) return new EvidenceFile(e, null, false, "The image file is missing.");
        var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var ok = Convert.ToHexString(SHA256.HashData(data)).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase);
        return new EvidenceFile(e, data, ok, ok ? "Image verified." : "WARNING: the image file was modified after capture.");
    }

    public static string FrameSummary(RawFrameEntity f) => string.Create(CultureInfo.InvariantCulture, $"#{f.Seq} {f.ReceivedUtc:HH:mm:ss.fff} {f.Display}");
}
