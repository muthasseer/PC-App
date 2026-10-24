using System.Text.Json;
using Weighbridge.Contracts;
using Weighbridge.Data;
using Weighbridge.Data.Stores;

namespace Weighbridge.Application.Sync;

/// <summary>Builds the cloud representation of a transaction and queues it (inside the caller's transaction).</summary>
public static class SyncMapper
{
    public static async Task EnqueueTransactionAsync(DbSession s, long transactionId, bool shareDriverNames, DateTimeOffset now, CancellationToken ct = default)
    {
        var dto = await ToDtoAsync(s, transactionId, shareDriverNames, ct).ConfigureAwait(false);
        await SyncQueueStore.EnqueueAsync(s, "Transaction", dto.Uid.ToString("D"), dto.Version, JsonSerializer.Serialize(dto, Api.Json), now, ct).ConfigureAwait(false);
    }

    public static async Task<SyncTransactionDto> ToDtoAsync(DbSession s, long transactionId, bool shareDriverNames, CancellationToken ct = default)
    {
        var t = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Transaction {transactionId} not found.");
        var measurements = await TransactionStore.MeasurementsAsync(s, transactionId, ct).ConfigureAwait(false);
        var byId = measurements.ToDictionary(m => m.Id, m => m.Uid);
        string? customerCode = t.CustomerId is { } c ? (await MasterDataStore.PartyAsync(s, PartyKind.Customer, c, ct).ConfigureAwait(false))?.Code : null;
        string? supplierCode = t.SupplierId is { } sp ? (await MasterDataStore.PartyAsync(s, PartyKind.Supplier, sp, ct).ConfigureAwait(false))?.Code : null;
        string? materialCode = t.MaterialId is { } m ? (await MasterDataStore.MaterialAsync(s, m, ct).ConfigureAwait(false))?.Code : null;
        string? orderNo = t.OrderId is { } o ? (await MasterDataStore.OrderAsync(s, o, ct).ConfigureAwait(false))?.OrderNo : null;
        string? replaces = t.ReplacesTransactionId is { } r ? (await TransactionStore.GetAsync(s, r, ct).ConfigureAwait(false))?.TicketNo : null;
        var completedBy = t.CompletedBy is { } cb ? (await UserStore.GetAsync(s, cb, ct).ConfigureAwait(false))?.Username : null;
        var cancelledBy = t.CancelledBy is { } xb ? (await UserStore.GetAsync(s, xb, ct).ConfigureAwait(false))?.Username : null;

        return new SyncTransactionDto(
            t.Uid, t.TicketNo, t.Version, t.Status.ToString(), t.Direction.ToString(), t.OperatingMode.ToString(), t.SiteCode ?? string.Empty, t.WeighbridgeCode ?? string.Empty,
            t.VehicleReg, shareDriverNames ? t.DriverName : null, customerCode, t.CustomerName, supplierCode, t.SupplierName, materialCode, t.MaterialName, orderNo,
            t.DeliveryNoteNo, t.ReferenceNo, t.Remarks, t.FirstG, t.SecondG, t.GrossG, t.TareG, t.NetG, t.FirstIsGross, t.TareMethod?.ToString(),
            t.HasManualWeight, t.HasOverride, t.CreatedByName ?? string.Empty, t.CreatedUtc, completedBy, t.CompletedUtc, cancelledBy, t.CancelledUtc, t.CancelReason,
            t.UpdatedUtc, replaces,
            measurements.Select(x => new SyncMeasurementDto(
                x.Uid, x.Stage, x.Source.ToString(), x.WeightG, x.ReportedValue, x.ReportedUnit, x.IsStable, x.UnstableOverride, x.OverrideReason, x.ManualReason,
                x.CapturedUtc, x.ReadingAgeMs, x.RawText, x.ProfileName, x.ProfileVersion, x.OperatingMode.ToString(), x.OperatorName ?? string.Empty,
                x.CorrectsMeasurementId is { } cm && byId.TryGetValue(cm, out var cu) ? cu : null, x.CorrectionReason)).ToList());
    }
}
