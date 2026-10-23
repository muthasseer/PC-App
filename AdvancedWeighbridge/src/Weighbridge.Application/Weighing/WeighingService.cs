using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Sync;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Devices.Cameras;

namespace Weighbridge.Application.Weighing;

/// <summary>Transaction details entered or confirmed by the operator.</summary>
public sealed record TransactionDetails
{
    public required string VehicleReg { get; init; }
    public TransactionDirection Direction { get; init; } = TransactionDirection.Inbound;
    public long? CustomerId { get; init; }
    public long? SupplierId { get; init; }
    public long? MaterialId { get; init; }
    public long? OrderId { get; init; }
    public long? DriverId { get; init; }
    public string? DriverName { get; init; }
    public long? TransportCompanyId { get; init; }
    public string? DeliveryNoteNo { get; init; }
    public string? ReferenceNo { get; init; }
    public string? Remarks { get; init; }
}

public sealed record CaptureRequest
{
    public required TransactionDetails Details { get; init; }
    public required OperatingMode Mode { get; init; }

    /// <summary>The indicator snapshot taken when GET WEIGHT was pressed (or by the automation engine).</summary>
    public required IndicatorSnapshot Snapshot { get; init; }

    public IReadOnlyList<RawFrame> EvidenceFrames { get; init; } = [];
    public IReadOnlyList<CameraSnapshot> Images { get; init; } = [];

    /// <summary>Supervisor authorisation to accept an unstable weight (policy SupervisorOverride).</summary>
    public SupervisorApproval? OverrideApproval { get; init; }
    public string? OverrideReason { get; init; }

    /// <summary>Complete a first weighing immediately with the vehicle's approved stored tare.</summary>
    public bool UseStoredTare { get; init; }

    /// <summary>The operator confirmed a possible duplicate (same vehicle completed a weighing very recently).</summary>
    public bool ConfirmDuplicate { get; init; }
}

public sealed record ManualEntryRequest
{
    public required TransactionDetails Details { get; init; }
    public required decimal WeightKg { get; init; }
    public required string Reason { get; init; }
    public SupervisorApproval? Approval { get; init; }
    public IReadOnlyList<CameraSnapshot> Images { get; init; } = [];
    public bool ConfirmDuplicate { get; init; }
}

public enum CaptureFailure
{
    None,
    Invalid,
    WaitForStability,
    OverrideRequired,
    Blacklisted,
    DuplicateSuspected,
    MissingDetails,
    NetInvalid,
    ApprovalRequired,
    Concurrency,
}

public sealed record CaptureResult(
    CaptureFailure Failure,
    string Message,
    TransactionEntity? Transaction,
    MeasurementEntity? Measurement,
    bool IsFinal,
    IReadOnlyList<string> Warnings,
    CaptureDecision? Decision = null)
{
    public bool Success => Failure == CaptureFailure.None;

    public static CaptureResult Fail(CaptureFailure failure, string message, CaptureDecision? decision = null) => new(failure, message, null, null, false, [], decision);
}

/// <summary>Context shown when a vehicle number is entered: pending first weighing, blacklist, stored tare, recent weighings.</summary>
public sealed record VehicleContext(VehicleEntity? Vehicle, TransactionEntity? Pending, TransactionEntity? RecentlyCompleted, bool StoredTareUsable, string? Warning)
{
    public WeighingStage NextStage => Pending is null ? WeighingStage.First : WeighingStage.Second;
}

/// <summary>
/// All weighing use cases. Every measurement is written with its evidence in one database transaction together
/// with the audit event and the cloud outbox entry.
/// </summary>
public sealed class WeighingService(
    Database db,
    SettingsService settings,
    AuditService audit,
    NotificationService notifications,
    AppPaths paths,
    TimeProvider time,
    ILogger<WeighingService> log)
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    public async Task<(WeighbridgeEntity Weighbridge, SiteEntity Site)> ActiveWeighbridgeAsync(CancellationToken ct = default)
    {
        var w = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        return await db.ReadAsync(async s =>
        {
            var wb = await SiteStore.GetWeighbridgeAsync(s, w.ActiveWeighbridgeId, ct).ConfigureAwait(false)
                     ?? (await SiteStore.WeighbridgesAsync(s, ct).ConfigureAwait(false)).FirstOrDefault()
                     ?? throw new InvalidOperationException("No weighbridge is configured.");
            var site = await SiteStore.GetSiteAsync(s, wb.SiteId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Site not found.");
            return (wb, site);
        }, ct).ConfigureAwait(false);
    }

    public async Task<CaptureLimits> LimitsAsync(CancellationToken ct = default)
    {
        var (wb, _) = await ActiveWeighbridgeAsync(ct).ConfigureAwait(false);
        var w = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        return new CaptureLimits { MinimumKg = wb.MinCaptureKg, CapacityKg = wb.CapacityKg, FreshnessMs = w.FreshnessMs, AllowNegative = w.AllowNegative };
    }

    public async Task<VehicleContext> VehicleContextAsync(string registration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registration)) return new VehicleContext(null, null, null, false, null);
        var (wb, site) = await ActiveWeighbridgeAsync(ct).ConfigureAwait(false);
        var w = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.ReadAsync(async s =>
        {
            var vehicle = await MasterDataStore.VehicleByRegistrationAsync(s, registration, ct).ConfigureAwait(false);
            var pending = await TransactionStore.PendingForVehicleAsync(s, site.Id, registration, ct).ConfigureAwait(false);
            var recent = await TransactionStore.LastCompletedForVehicleAsync(s, registration, now.AddMinutes(-w.DuplicateWindowMinutes), ct).ConfigureAwait(false);
            string? warning = null;
            if (vehicle?.IsBlacklisted == true) warning = $"Vehicle is blacklisted: {vehicle.BlacklistReason}";
            else if (pending is null && recent is not null) warning = $"This vehicle completed ticket {recent.TicketNo} at {recent.CompletedUtc?.ToLocalTime():HH:mm}. Possible duplicate.";
            return new VehicleContext(vehicle, pending, recent, vehicle?.HasUsableStoredTare(now) == true, warning);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ capture from the indicator

    public async Task<CaptureResult> CaptureAsync(UserContext user, CaptureRequest request, CancellationToken ct = default)
    {
        user.Demand(request.Mode == OperatingMode.Automatic ? Permissions.WeighingAutomatic : Permissions.WeighingCapture);
        if (request.Mode == OperatingMode.Manual) throw new ArgumentException("Use ManualEntryAsync for manual mode.");
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        var limits = await LimitsAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();

        var decision = CaptureValidator.Evaluate(request.Snapshot, now, limits, weighing.Capture);
        var overridden = false;
        switch (decision.Verdict)
        {
            case CaptureVerdict.Rejected:
                return CaptureResult.Fail(CaptureFailure.Invalid, decision.Summary, decision);
            case CaptureVerdict.WaitForStability:
                return CaptureResult.Fail(CaptureFailure.WaitForStability, decision.Summary, decision);
            case CaptureVerdict.OverrideRequired:
                if (request.OverrideApproval is null || string.IsNullOrWhiteSpace(request.OverrideReason) || request.OverrideReason.Trim().Length < approvals.MinimumReasonLength)
                {
                    return CaptureResult.Fail(CaptureFailure.OverrideRequired, decision.Summary + " Supervisor authorisation and a reason are required.", decision);
                }
                overridden = true;
                break;
        }

        var captured = CapturedWeight.FromDecision(decision, request.Snapshot, now, request.Mode, request.EvidenceFrames, overridden, request.OverrideReason);
        var measurement = ToMeasurement(captured, user, request.OverrideApproval);
        return await RecordAsync(user, request.Details, measurement, request.Images, request.Mode, request.UseStoredTare, request.ConfirmDuplicate,
            request.EvidenceFrames, decision, weighing, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ manual entry

    public async Task<CaptureResult> ManualEntryAsync(UserContext user, ManualEntryRequest request, CancellationToken ct = default)
    {
        user.Demand(Permissions.WeighingManualEntry);
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        var limits = await LimitsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < approvals.MinimumReasonLength)
        {
            return CaptureResult.Fail(CaptureFailure.MissingDetails, $"A reason of at least {approvals.MinimumReasonLength} characters is required for a manual weight.");
        }
        if (approvals.ManualEntryRequiresSupervisor && request.Approval is null)
        {
            return CaptureResult.Fail(CaptureFailure.ApprovalRequired, "A supervisor must authorise this manual weight.");
        }
        if (request.WeightKg <= 0 || request.WeightKg > limits.CapacityKg)
        {
            return CaptureResult.Fail(CaptureFailure.Invalid, $"The weight must be between 0 and the capacity of {limits.CapacityKg:#,0} kg.");
        }

        var now = time.GetUtcNow();
        var measurement = new MeasurementEntity
        {
            Source = MeasurementSource.Manual,
            WeightG = Weight.FromKilograms(request.WeightKg).ToGrams(),
            ReportedValue = request.WeightKg.ToString(CultureInfo.InvariantCulture),
            ReportedUnit = "kg",
            IsStable = false,
            StabilityReason = "Manual",
            ManualReason = request.Reason.Trim(),
            ManualApprovedBy = request.Approval?.UserId,
            CapturedUtc = now,
            OperatingMode = OperatingMode.Manual,
            OperatorId = user.UserId,
            Machine = Environment.MachineName,
        };
        return await RecordAsync(user, request.Details, measurement, request.Images, OperatingMode.Manual, useStoredTare: false, request.ConfirmDuplicate, [], null, weighing, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ shared recording

    private async Task<CaptureResult> RecordAsync(UserContext user, TransactionDetails details, MeasurementEntity measurement, IReadOnlyList<CameraSnapshot> images,
        OperatingMode mode, bool useStoredTare, bool confirmDuplicate, IReadOnlyList<RawFrame> frames, CaptureDecision? decision, WeighingSettings weighing, CancellationToken ct)
    {
        var registration = VehicleRegistration.Display(details.VehicleReg);
        if (VehicleRegistration.Normalize(registration).Length < 2) return CaptureResult.Fail(CaptureFailure.MissingDetails, "Enter the vehicle number.", decision);
        var (wb, site) = await ActiveWeighbridgeAsync(ct).ConfigureAwait(false);
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var warnings = new List<string>();
        var savedFiles = new List<string>();

        try
        {
            var result = await db.InTransactionAsync(async s =>
            {
                var vehicle = await MasterDataStore.VehicleByRegistrationAsync(s, registration, ct).ConfigureAwait(false);
                if (vehicle?.IsBlacklisted == true)
                {
                    return CaptureResult.Fail(CaptureFailure.Blacklisted, $"Vehicle {registration} is blacklisted: {vehicle.BlacklistReason}", decision);
                }

                var pending = await TransactionStore.PendingForVehicleAsync(s, site.Id, registration, ct).ConfigureAwait(false);
                TransactionEntity txn;
                bool isFinal;
                if (pending is null)
                {
                    if (mode != OperatingMode.Automatic)
                    {
                        if (weighing.RequireMaterial && details.MaterialId is null) return CaptureResult.Fail(CaptureFailure.MissingDetails, "Select the material.", decision);
                        if (weighing.RequireCustomerOrSupplier && details.CustomerId is null && details.SupplierId is null) return CaptureResult.Fail(CaptureFailure.MissingDetails, "Select the customer or supplier.", decision);
                    }
                    var recent = await TransactionStore.LastCompletedForVehicleAsync(s, registration, now.AddMinutes(-weighing.DuplicateWindowMinutes), ct).ConfigureAwait(false);
                    if (recent is not null && !confirmDuplicate)
                    {
                        return CaptureResult.Fail(CaptureFailure.DuplicateSuspected,
                            $"Vehicle {registration} completed ticket {recent.TicketNo} {Math.Max(0, (int)(now - recent.CompletedUtc!.Value).TotalMinutes)} minutes ago. Confirm that this is a new weighing.", decision);
                    }
                    if (vehicle is null && weighing.AutoCreateVehicles)
                    {
                        vehicle = new VehicleEntity { Registration = registration, CreatedUtc = now, UpdatedUtc = now };
                        vehicle.Id = await MasterDataStore.InsertVehicleAsync(s, vehicle, ct).ConfigureAwait(false);
                    }

                    var resolved = await ResolveDetailsAsync(s, details, mode, weighing, registration, ct).ConfigureAwait(false);
                    var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime;
                    var scope = TicketNumbering.ScopeKey(weighing.TicketNumbers, site.Code, wb.Code, local);
                    var seq = await TransactionStore.NextSequenceAsync(s, scope, ct).ConfigureAwait(false);
                    txn = new TransactionEntity
                    {
                        TicketNo = TicketNumbering.Format(weighing.TicketNumbers, site.Code, wb.Code, local, seq),
                        SiteId = site.Id,
                        WeighbridgeId = wb.Id,
                        Status = TransactionStatus.Pending,
                        Direction = resolved.Direction,
                        OperatingMode = mode,
                        VehicleId = vehicle?.Id,
                        VehicleReg = registration,
                        VehicleRegNorm = VehicleRegistration.Normalize(registration),
                        CreatedBy = user.UserId,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                    };
                    ApplyDetails(txn, resolved.Details, resolved.Names);
                    txn.Id = await TransactionStore.InsertAsync(s, txn, ct).ConfigureAwait(false);

                    measurement.TransactionId = txn.Id;
                    measurement.Stage = 1;
                    measurement.Id = await TransactionStore.InsertMeasurementAsync(s, measurement, ct).ConfigureAwait(false);
                    await SaveEvidenceAsync(s, txn, measurement, frames, images, savedFiles, ct).ConfigureAwait(false);
                    await TransactionStore.SetFirstMeasurementAsync(s, txn.Id, measurement.Id, measurement.WeightG, measurement.Source == MeasurementSource.Manual, measurement.UnstableOverride, now, 1, ct).ConfigureAwait(false);
                    await audit.WriteAsync(s, user, "Weighing", "FirstWeight", $"Ticket {txn.TicketNo}: first weight {Format(measurement.WeightG)} for {registration} ({measurement.Source}, {mode})",
                        "Transaction", txn.Uid.ToString("D"), newJson: AuditService.ToJson(new { measurement.WeightG, measurement.Source, measurement.RawText, measurement.ReadingAgeMs, measurement.ProfileName, measurement.ProfileVersion }),
                        reason: measurement.ManualReason ?? measurement.OverrideReason, ct: ct).ConfigureAwait(false);
                    isFinal = false;

                    if (useStoredTare && vehicle is not null && vehicle.HasUsableStoredTare(now) && measurement.Source != MeasurementSource.Manual)
                    {
                        var completed = await CompleteWithTareAsync(s, user, txn, measurement, vehicle, now, weighing, ct).ConfigureAwait(false);
                        if (!completed.Valid) return CaptureResult.Fail(CaptureFailure.NetInvalid, completed.Message, decision);
                        warnings.AddRange(completed.Warnings);
                        isFinal = true;
                    }
                }
                else
                {
                    txn = pending;
                    // Fields left empty at the second weighing keep the values recorded at the first weighing.
                    var merged = Merge(pending, details, mode);
                    var first = (await TransactionStore.MeasurementsAsync(s, txn.Id, ct).ConfigureAwait(false)).Single(m => m.Id == txn.FirstMeasurementId);
                    var net = NetCalculator.Calculate(first.Weight, Weight.FromGrams(measurement.WeightG), merged.Direction, weighing.Net, first.CapturedUtc, now);
                    if (!net.IsValid) return CaptureResult.Fail(CaptureFailure.NetInvalid, string.Join(" ", net.Errors), decision);
                    warnings.AddRange(net.Warnings);

                    // Details confirmed at the second weighing are applied (and logged) before completion.
                    var resolved = await ResolveDetailsAsync(s, merged, OperatingMode.SemiAutomatic, weighing, registration, ct).ConfigureAwait(false);
                    var version = await ApplyDetailChangesAsync(s, user, txn, resolved.Details, resolved.Names, "Confirmed at second weighing", null, now, ct).ConfigureAwait(false);
                    if (mode != OperatingMode.Automatic && weighing.RequireMaterial && txn.MaterialId is null) return CaptureResult.Fail(CaptureFailure.MissingDetails, "Select the material.", decision);

                    measurement.TransactionId = txn.Id;
                    measurement.Stage = 2;
                    measurement.Id = await TransactionStore.InsertMeasurementAsync(s, measurement, ct).ConfigureAwait(false);
                    await SaveEvidenceAsync(s, txn, measurement, frames, images, savedFiles, ct).ConfigureAwait(false);
                    var tareIsManual = (net.FirstIsGross ? measurement.Source : first.Source) == MeasurementSource.Manual;
                    await TransactionStore.CompleteAsync(s, new TransactionStore.Completion(txn.Id, measurement.Id, measurement.WeightG, net.Gross.ToGrams(), net.Tare.ToGrams(), net.Net.ToGrams(),
                        net.FirstIsGross, tareIsManual ? TareMethod.ManualTare : TareMethod.Measured, measurement.Source == MeasurementSource.Manual, measurement.UnstableOverride,
                        user.UserId, now, version), ct).ConfigureAwait(false);
                    await audit.WriteAsync(s, user, "Weighing", "Completed",
                        $"Ticket {txn.TicketNo}: second weight {Format(measurement.WeightG)}, gross {Format(net.Gross.ToGrams())}, tare {Format(net.Tare.ToGrams())}, net {Format(net.Net.ToGrams())}",
                        "Transaction", txn.Uid.ToString("D"), newJson: AuditService.ToJson(new { measurement.WeightG, measurement.Source, GrossG = net.Gross.ToGrams(), TareG = net.Tare.ToGrams(), NetG = net.Net.ToGrams(), measurement.RawText }),
                        reason: measurement.ManualReason ?? measurement.OverrideReason, ct: ct).ConfigureAwait(false);
                    isFinal = true;
                }

                if (measurement.UnstableOverride || measurement.Source == MeasurementSource.Manual)
                {
                    await ApprovalStore.AddAsync(s, new ApprovalEntity
                    {
                        RequestType = measurement.Source == MeasurementSource.Manual ? "ManualWeight" : "UnstableOverride",
                        EntityType = "Transaction", EntityId = txn.Uid.ToString("D"), RequestedBy = user.UserId, RequestedUtc = now,
                        Status = "Approved",
                        DecidedBy = measurement.ManualApprovedBy ?? measurement.OverrideApprovedBy ?? user.UserId, DecidedUtc = now,
                        DecisionReason = measurement.ManualReason ?? measurement.OverrideReason,
                        PayloadJson = AuditService.ToJson(new { measurement.WeightG, measurement.Stage }),
                    }, ct).ConfigureAwait(false);
                }

                await SyncMapper.EnqueueTransactionAsync(s, txn.Id, cloud.ShareDriverNames, now, ct).ConfigureAwait(false);
                var saved = await TransactionStore.GetAsync(s, txn.Id, ct).ConfigureAwait(false);
                return new CaptureResult(CaptureFailure.None, isFinal ? $"Ticket {saved!.TicketNo} completed. Net {Format(saved.NetG ?? 0)}." : $"First weight saved on ticket {saved!.TicketNo}.",
                    saved, measurement, isFinal, warnings, decision);
            }, ct).ConfigureAwait(false);

            if (!result.Success) DeleteFiles(savedFiles);
            else if (result.Measurement?.Source == MeasurementSource.Manual)
            {
                await notifications.NotifyAsync("Warning", "ManualWeight", "Manual weight entered", $"{registration}: {Format(result.Measurement.WeightG)} entered manually by {user.DisplayName}.", "Transaction", result.Transaction?.Uid.ToString("D"), ct).ConfigureAwait(false);
            }
            return result;
        }
        catch (ConcurrencyException e)
        {
            DeleteFiles(savedFiles);
            return CaptureResult.Fail(CaptureFailure.Concurrency, "The transaction was changed by someone else. Please try again. " + e.Message, decision);
        }
        catch (System.Data.Common.DbException e) when (e.Message.Contains("ux_txn_pending_vehicle", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            DeleteFiles(savedFiles);
            log.LogWarning(e, "Duplicate pending transaction prevented");
            return CaptureResult.Fail(CaptureFailure.Concurrency, "A pending weighing for this vehicle was created at the same time. Please try again.", decision);
        }
        catch
        {
            DeleteFiles(savedFiles);
            throw;
        }
    }

    private async Task<(bool Valid, string Message, IReadOnlyList<string> Warnings)> CompleteWithTareAsync(DbSession s, UserContext user, TransactionEntity txn, MeasurementEntity first,
        VehicleEntity vehicle, DateTimeOffset now, WeighingSettings weighing, CancellationToken ct)
    {
        var tare = vehicle.StoredTare!.Value;
        var net = NetCalculator.WithKnownTare(first.Weight, tare, weighing.Net);
        if (!net.IsValid) return (false, string.Join(" ", net.Errors), []);
        var tareMeasurement = new MeasurementEntity
        {
            TransactionId = txn.Id,
            Stage = 2,
            Source = MeasurementSource.StoredTare,
            WeightG = tare.ToGrams(),
            ReportedValue = tare.Kilograms.ToString(CultureInfo.InvariantCulture),
            ReportedUnit = "kg",
            IsStable = true,
            StabilityReason = $"Stored tare approved {vehicle.StoredTareApprovedUtc:yyyy-MM-dd}, expires {vehicle.StoredTareExpiresUtc:yyyy-MM-dd}",
            CapturedUtc = now,
            OperatingMode = first.OperatingMode,
            OperatorId = user.UserId,
            Machine = Environment.MachineName,
        };
        tareMeasurement.Id = await TransactionStore.InsertMeasurementAsync(s, tareMeasurement, ct).ConfigureAwait(false);
        await TransactionStore.CompleteAsync(s, new TransactionStore.Completion(txn.Id, tareMeasurement.Id, tareMeasurement.WeightG, net.Gross.ToGrams(), net.Tare.ToGrams(), net.Net.ToGrams(),
            true, TareMethod.StoredTare, false, first.UnstableOverride, user.UserId, now, 2), ct).ConfigureAwait(false);
        await audit.WriteAsync(s, user, "Weighing", "CompletedStoredTare", $"Ticket {txn.TicketNo}: completed with stored tare {Format(tare.ToGrams())}, net {Format(net.Net.ToGrams())}",
            "Transaction", txn.Uid.ToString("D"), ct: ct).ConfigureAwait(false);
        return (true, string.Empty, net.Warnings);
    }

    // ------------------------------------------------------------------ cancel, edit, correct

    public async Task<TransactionEntity> CancelAsync(UserContext user, long transactionId, string reason, SupervisorApproval? approval, CancellationToken ct = default)
    {
        user.Demand(Permissions.TransactionCancel);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        RequireReason(reason, approvals);
        if (approvals.CancelRequiresSupervisor && approval is null && !user.Has(Permissions.ApprovalsDecide)) throw new ApprovalRequiredException("Cancelling a ticket requires supervisor authorisation.");
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var t = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            await TransactionStore.CancelAsync(s, t.Id, user.UserId, reason.Trim(), now, t.Version, ct).ConfigureAwait(false);
            await ApprovalStore.AddAsync(s, new ApprovalEntity
            {
                RequestType = "Cancel", EntityType = "Transaction", EntityId = t.Uid.ToString("D"), RequestedBy = user.UserId, RequestedUtc = now, Status = "Approved",
                DecidedBy = approval?.UserId ?? user.UserId, DecidedUtc = now, DecisionReason = reason.Trim(),
            }, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Weighing", "Cancelled", $"Ticket {t.TicketNo} cancelled{(approval is null ? "" : $" (authorised by {approval.Username})")}", "Transaction", t.Uid.ToString("D"),
                AuditService.ToJson(new { t.Status, t.NetG }), null, reason, ct).ConfigureAwait(false);
            await SyncMapper.EnqueueTransactionAsync(s, t.Id, cloud.ShareDriverNames, now, ct).ConfigureAwait(false);
            return (await TransactionStore.GetAsync(s, t.Id, ct).ConfigureAwait(false))!;
        }, ct).ConfigureAwait(false);
    }

    public async Task<TransactionEntity> UpdateDetailsAsync(UserContext user, long transactionId, TransactionDetails details, string? reason, SupervisorApproval? approval, CancellationToken ct = default)
    {
        user.Demand(Permissions.TransactionEdit);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var t = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            if (t.Status == TransactionStatus.Cancelled) throw new InvalidOperationException("A cancelled ticket cannot be changed.");
            if (t.Status == TransactionStatus.Completed)
            {
                RequireReason(reason, approvals);
                if (approvals.EditCompletedRequiresSupervisor && approval is null && !user.Has(Permissions.ApprovalsDecide)) throw new ApprovalRequiredException("Changing a completed ticket requires supervisor authorisation.");
            }
            if (VehicleRegistration.Normalize(details.VehicleReg) != t.VehicleRegNorm && t.Status != TransactionStatus.Pending)
            {
                throw new InvalidOperationException("The vehicle of a completed ticket cannot be changed. Cancel and re-issue the ticket instead.");
            }
            var resolved = await ResolveDetailsAsync(s, details, OperatingMode.SemiAutomatic, weighing, details.VehicleReg, ct).ConfigureAwait(false);
            await ApplyDetailChangesAsync(s, user, t, resolved.Details, resolved.Names, reason, approval?.UserId, now, ct).ConfigureAwait(false);
            await SyncMapper.EnqueueTransactionAsync(s, t.Id, cloud.ShareDriverNames, now, ct).ConfigureAwait(false);
            return (await TransactionStore.GetAsync(s, t.Id, ct).ConfigureAwait(false))!;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the first weight of a pending transaction. The original measurement is kept and linked.</summary>
    public async Task<TransactionEntity> CorrectFirstWeightAsync(UserContext user, long transactionId, decimal weightKg, string reason, SupervisorApproval approval, CancellationToken ct = default)
    {
        user.Demand(Permissions.TransactionCorrectWeight);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        RequireReason(reason, approvals);
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var t = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            if (t.Status != TransactionStatus.Pending) throw new InvalidOperationException("Only the first weight of a pending ticket can be corrected. Use cancel and re-issue for completed tickets.");
            var corrected = NewCorrection(user, t.Id, 1, weightKg, reason, approval, t.FirstMeasurementId!.Value, now);
            corrected.Id = await TransactionStore.InsertMeasurementAsync(s, corrected, ct).ConfigureAwait(false);
            await TransactionStore.SetFirstMeasurementAsync(s, t.Id, corrected.Id, corrected.WeightG, manual: true, overridden: false, now, t.Version, ct).ConfigureAwait(false);
            await audit.WriteAsync(s, user, "Weighing", "WeightCorrected", $"Ticket {t.TicketNo}: first weight corrected from {Format(t.FirstG ?? 0)} to {Format(corrected.WeightG)} (authorised by {approval.Username})",
                "Transaction", t.Uid.ToString("D"), AuditService.ToJson(new { t.FirstG }), AuditService.ToJson(new { corrected.WeightG }), reason, ct).ConfigureAwait(false);
            await SyncMapper.EnqueueTransactionAsync(s, t.Id, cloud.ShareDriverNames, now, ct).ConfigureAwait(false);
            return (await TransactionStore.GetAsync(s, t.Id, ct).ConfigureAwait(false))!;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Corrects a completed ticket without altering it: the original is cancelled and a new ticket is issued that
    /// references it. Measurements are carried over (linked to the originals); corrected ones become manual entries.
    /// </summary>
    public async Task<TransactionEntity> ReissueAsync(UserContext user, long transactionId, decimal? firstKg, decimal? secondKg, string reason, SupervisorApproval approval, CancellationToken ct = default)
    {
        user.Demand(Permissions.TransactionCorrectWeight);
        user.Demand(Permissions.TransactionCancel);
        var approvals = await settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals, ct).ConfigureAwait(false);
        RequireReason(reason, approvals);
        var weighing = await settings.GetAsync<WeighingSettings>(SettingKeys.Weighing, ct).ConfigureAwait(false);
        var cloud = await settings.GetAsync<CloudSettings>(SettingKeys.Cloud, ct).ConfigureAwait(false);
        var (wb, site) = await ActiveWeighbridgeAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(async s =>
        {
            var original = await TransactionStore.GetAsync(s, transactionId, ct).ConfigureAwait(false) ?? throw new ArgumentException("Transaction not found.");
            if (original.Status != TransactionStatus.Completed) throw new InvalidOperationException("Only completed tickets are re-issued.");
            var ms = await TransactionStore.MeasurementsAsync(s, original.Id, ct).ConfigureAwait(false);
            var first = ms.Single(m => m.Id == original.FirstMeasurementId);
            var second = ms.Single(m => m.Id == original.SecondMeasurementId);

            await TransactionStore.CancelAsync(s, original.Id, user.UserId, $"Corrected and re-issued: {reason.Trim()}", now, original.Version, ct).ConfigureAwait(false);

            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime;
            var seq = await TransactionStore.NextSequenceAsync(s, TicketNumbering.ScopeKey(weighing.TicketNumbers, site.Code, wb.Code, local), ct).ConfigureAwait(false);
            var copy = new TransactionEntity
            {
                TicketNo = TicketNumbering.Format(weighing.TicketNumbers, site.Code, wb.Code, local, seq),
                SiteId = original.SiteId, WeighbridgeId = original.WeighbridgeId, Status = TransactionStatus.Pending, Direction = original.Direction,
                OperatingMode = original.OperatingMode, VehicleId = original.VehicleId, VehicleReg = original.VehicleReg, VehicleRegNorm = original.VehicleRegNorm,
                DriverId = original.DriverId, DriverName = original.DriverName, TransportCompanyId = original.TransportCompanyId, CustomerId = original.CustomerId,
                CustomerName = original.CustomerName, SupplierId = original.SupplierId, SupplierName = original.SupplierName, MaterialId = original.MaterialId,
                MaterialName = original.MaterialName, OrderId = original.OrderId, DeliveryNoteNo = original.DeliveryNoteNo, ReferenceNo = original.ReferenceNo,
                Remarks = original.Remarks, ReplacesTransactionId = original.Id, CreatedBy = user.UserId, CreatedUtc = now, UpdatedUtc = now,
            };
            copy.Id = await TransactionStore.InsertAsync(s, copy, ct).ConfigureAwait(false);

            MeasurementEntity Carry(MeasurementEntity m, decimal? correctedKg) => correctedKg is { } kg
                ? NewCorrection(user, copy.Id, m.Stage, kg, reason, approval, m.Id, now)
                : new MeasurementEntity
                {
                    TransactionId = copy.Id, Stage = m.Stage, Source = m.Source, WeightG = m.WeightG, ReportedValue = m.ReportedValue, ReportedUnit = m.ReportedUnit,
                    ReportedDecimals = m.ReportedDecimals, IsStable = m.IsStable, StabilityReason = m.StabilityReason, UnstableOverride = m.UnstableOverride,
                    OverrideReason = m.OverrideReason, ManualReason = m.ManualReason, CapturedUtc = m.CapturedUtc, ReadingReceivedUtc = m.ReadingReceivedUtc,
                    ReadingAgeMs = m.ReadingAgeMs, RawText = m.RawText, RawHex = m.RawHex, IndicatorFlags = m.IndicatorFlags, PortDescription = m.PortDescription,
                    ProfileUid = m.ProfileUid, ProfileName = m.ProfileName, ProfileVersion = m.ProfileVersion, OperatingMode = m.OperatingMode, OperatorId = m.OperatorId,
                    Machine = m.Machine, CorrectsMeasurementId = m.Id, CorrectionReason = $"Carried over from ticket {original.TicketNo}",
                };

            var m1 = Carry(first, firstKg);
            m1.Id = await TransactionStore.InsertMeasurementAsync(s, m1, ct).ConfigureAwait(false);
            await TransactionStore.SetFirstMeasurementAsync(s, copy.Id, m1.Id, m1.WeightG, m1.Source == MeasurementSource.Manual, m1.UnstableOverride, now, 1, ct).ConfigureAwait(false);
            var m2 = Carry(second, secondKg);
            m2.Id = await TransactionStore.InsertMeasurementAsync(s, m2, ct).ConfigureAwait(false);
            var net = second.Source == MeasurementSource.StoredTare && secondKg is null
                ? NetCalculator.WithKnownTare(m1.Weight, m2.Weight, weighing.Net)
                : NetCalculator.Calculate(m1.Weight, m2.Weight, original.Direction, weighing.Net);
            if (!net.IsValid) throw new InvalidOperationException(string.Join(" ", net.Errors));
            var tareSource = (net.FirstIsGross ? m2 : m1).Source;
            await TransactionStore.CompleteAsync(s, new TransactionStore.Completion(copy.Id, m2.Id, m2.WeightG, net.Gross.ToGrams(), net.Tare.ToGrams(), net.Net.ToGrams(), net.FirstIsGross,
                tareSource switch { MeasurementSource.Manual => TareMethod.ManualTare, MeasurementSource.StoredTare => TareMethod.StoredTare, _ => TareMethod.Measured },
                m1.Source == MeasurementSource.Manual || m2.Source == MeasurementSource.Manual, m1.UnstableOverride || m2.UnstableOverride, user.UserId, now, 2), ct).ConfigureAwait(false);

            await audit.WriteAsync(s, user, "Weighing", "Reissued", $"Ticket {original.TicketNo} cancelled and re-issued as {copy.TicketNo} (net {Format(original.NetG ?? 0)} → {Format(net.Net.ToGrams())}, authorised by {approval.Username})",
                "Transaction", original.Uid.ToString("D"), AuditService.ToJson(new { original.FirstG, original.SecondG, original.NetG }), AuditService.ToJson(new { FirstG = m1.WeightG, SecondG = m2.WeightG, NetG = net.Net.ToGrams(), copy.TicketNo }), reason, ct).ConfigureAwait(false);
            await SyncMapper.EnqueueTransactionAsync(s, original.Id, cloud.ShareDriverNames, now, ct).ConfigureAwait(false);
            await SyncMapper.EnqueueTransactionAsync(s, copy.Id, cloud.ShareDriverNames, now, ct).ConfigureAwait(false);
            return (await TransactionStore.GetAsync(s, copy.Id, ct).ConfigureAwait(false))!;
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private static MeasurementEntity ToMeasurement(CapturedWeight c, UserContext user, SupervisorApproval? overrideApproval) => new()
    {
        Source = c.Source,
        WeightG = c.Weight.ToGrams(),
        ReportedValue = c.ReportedValue?.ToString(CultureInfo.InvariantCulture),
        ReportedUnit = WeightUnits.Symbol(c.ReportedUnit),
        ReportedDecimals = c.ReportedDecimals,
        IsStable = c.IsStable,
        StabilityReason = c.StabilityReason.ToString(),
        StabilitySpanG = Weight.FromKilograms(c.StabilitySpanKg).ToGrams(),
        StableForMs = c.StableForMs,
        UnstableOverride = c.UnstableOverride,
        OverrideReason = c.OverrideReason,
        OverrideApprovedBy = overrideApproval?.UserId,
        CapturedUtc = c.CapturedAt,
        ReadingReceivedUtc = c.ReadingReceivedAt,
        ReadingAgeMs = c.ReadingAgeMs,
        RawText = c.RawText,
        RawHex = c.RawHex,
        IndicatorFlags = (int)c.Flags,
        PortDescription = c.PortDescription,
        ProfileUid = c.ProfileId?.ToString("D"),
        ProfileName = c.ProfileName,
        ProfileVersion = c.ProfileVersion,
        OperatingMode = c.Mode,
        OperatorId = user.UserId,
        Machine = Environment.MachineName,
    };

    private static MeasurementEntity NewCorrection(UserContext user, long transactionId, int stage, decimal kg, string reason, SupervisorApproval approval, long correctsId, DateTimeOffset now) => new()
    {
        TransactionId = transactionId,
        Stage = stage,
        Source = MeasurementSource.Manual,
        WeightG = Weight.FromKilograms(kg).ToGrams(),
        ReportedValue = kg.ToString(CultureInfo.InvariantCulture),
        ReportedUnit = "kg",
        IsStable = false,
        StabilityReason = "Correction",
        ManualReason = reason.Trim(),
        ManualApprovedBy = approval.UserId,
        CapturedUtc = now,
        OperatingMode = OperatingMode.Manual,
        OperatorId = user.UserId,
        Machine = Environment.MachineName,
        CorrectsMeasurementId = correctsId,
        CorrectionReason = reason.Trim(),
    };

    private sealed record DetailNames(string? Customer, string? Supplier, string? Material);

    private static async Task<(TransactionDetails Details, DetailNames Names, TransactionDirection Direction)> ResolveDetailsAsync(DbSession s, TransactionDetails d, OperatingMode mode,
        WeighingSettings weighing, string registration, CancellationToken ct)
    {
        if (mode == OperatingMode.Automatic && weighing.AutoFillFromLastTransaction && d.MaterialId is null && d.CustomerId is null && d.SupplierId is null)
        {
            var last = await TransactionStore.LastCompletedForVehicleAsync(s, registration, DateTimeOffset.MinValue, ct).ConfigureAwait(false);
            if (last is not null)
            {
                d = d with { CustomerId = last.CustomerId, SupplierId = last.SupplierId, MaterialId = last.MaterialId, Direction = last.Direction, DriverName = d.DriverName ?? last.DriverName };
            }
        }
        var customer = d.CustomerId is { } c ? (await MasterDataStore.PartyAsync(s, PartyKind.Customer, c, ct).ConfigureAwait(false))?.Name : null;
        var supplier = d.SupplierId is { } sp ? (await MasterDataStore.PartyAsync(s, PartyKind.Supplier, sp, ct).ConfigureAwait(false))?.Name : null;
        var material = d.MaterialId is { } m ? (await MasterDataStore.MaterialAsync(s, m, ct).ConfigureAwait(false))?.Name : null;
        return (d, new DetailNames(customer, supplier, material), d.Direction);
    }

    private static TransactionDetails Merge(TransactionEntity t, TransactionDetails d, OperatingMode mode) => new()
    {
        VehicleReg = t.VehicleReg,
        Direction = mode == OperatingMode.Automatic ? t.Direction : d.Direction,
        CustomerId = d.CustomerId ?? t.CustomerId,
        SupplierId = d.SupplierId ?? t.SupplierId,
        MaterialId = d.MaterialId ?? t.MaterialId,
        OrderId = d.OrderId ?? t.OrderId,
        DriverId = d.DriverId ?? t.DriverId,
        DriverName = Clean(d.DriverName) ?? t.DriverName,
        TransportCompanyId = d.TransportCompanyId ?? t.TransportCompanyId,
        DeliveryNoteNo = Clean(d.DeliveryNoteNo) ?? t.DeliveryNoteNo,
        ReferenceNo = Clean(d.ReferenceNo) ?? t.ReferenceNo,
        Remarks = Clean(d.Remarks) ?? t.Remarks,
    };

    private static void ApplyDetails(TransactionEntity t, TransactionDetails d, DetailNames names)
    {
        t.Direction = d.Direction;
        t.CustomerId = d.CustomerId;
        t.CustomerName = names.Customer;
        t.SupplierId = d.SupplierId;
        t.SupplierName = names.Supplier;
        t.MaterialId = d.MaterialId;
        t.MaterialName = names.Material;
        t.OrderId = d.OrderId;
        t.DriverId = d.DriverId;
        t.DriverName = string.IsNullOrWhiteSpace(d.DriverName) ? null : d.DriverName.Trim();
        t.TransportCompanyId = d.TransportCompanyId;
        t.DeliveryNoteNo = Clean(d.DeliveryNoteNo);
        t.ReferenceNo = Clean(d.ReferenceNo);
        t.Remarks = Clean(d.Remarks);
        t.VehicleReg = VehicleRegistration.Display(d.VehicleReg);
        t.VehicleRegNorm = VehicleRegistration.Normalize(d.VehicleReg);
    }

    /// <summary>Applies changed fields, logs each in transaction_changes and returns the new version.</summary>
    private async Task<int> ApplyDetailChangesAsync(DbSession s, UserContext user, TransactionEntity t, TransactionDetails d, DetailNames names, string? reason, long? approvedBy,
        DateTimeOffset now, CancellationToken ct)
    {
        var before = new Dictionary<string, string?>
        {
            ["Vehicle"] = t.VehicleReg, ["Direction"] = t.Direction.ToString(), ["Customer"] = t.CustomerName, ["Supplier"] = t.SupplierName, ["Material"] = t.MaterialName,
            ["Order"] = t.OrderId?.ToString(CultureInfo.InvariantCulture), ["Driver"] = t.DriverName, ["DeliveryNote"] = t.DeliveryNoteNo, ["Reference"] = t.ReferenceNo, ["Remarks"] = t.Remarks,
        };
        ApplyDetails(t, d, names);
        var after = new Dictionary<string, string?>
        {
            ["Vehicle"] = t.VehicleReg, ["Direction"] = t.Direction.ToString(), ["Customer"] = t.CustomerName, ["Supplier"] = t.SupplierName, ["Material"] = t.MaterialName,
            ["Order"] = t.OrderId?.ToString(CultureInfo.InvariantCulture), ["Driver"] = t.DriverName, ["DeliveryNote"] = t.DeliveryNoteNo, ["Reference"] = t.ReferenceNo, ["Remarks"] = t.Remarks,
        };
        var changed = after.Where(kv => before[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        if (changed.Count == 0) return t.Version;

        await TransactionStore.UpdateDetailsAsync(s, t, now, t.Version, ct).ConfigureAwait(false);
        foreach (var field in changed)
        {
            await TransactionStore.AddChangeAsync(s, new TransactionChangeEntity
            {
                TransactionId = t.Id, Field = field, OldValue = before[field], NewValue = after[field], Reason = reason, UserId = user.UserId, ApprovedBy = approvedBy, AtUtc = now,
            }, ct).ConfigureAwait(false);
        }
        await audit.WriteAsync(s, user, "Weighing", "DetailsChanged", $"Ticket {t.TicketNo}: {string.Join(", ", changed)} changed", "Transaction", t.Uid.ToString("D"),
            AuditService.ToJson(changed.ToDictionary(f => f, f => before[f])), AuditService.ToJson(changed.ToDictionary(f => f, f => after[f])), reason, ct).ConfigureAwait(false);
        return t.Version + 1;
    }

    private async Task SaveEvidenceAsync(DbSession s, TransactionEntity t, MeasurementEntity m, IReadOnlyList<RawFrame> frames, IReadOnlyList<CameraSnapshot> images, List<string> saved, CancellationToken ct)
    {
        if (frames.Count > 0)
        {
            var capturedSeq = frames.Max(f => f.Sequence);
            await TransactionStore.InsertRawFramesAsync(s, m.Id, frames.Select(f => new RawFrameEntity
            {
                Seq = f.Sequence, ReceivedUtc = f.ReceivedAt, Hex = ByteText.ToHex(f.Full, string.Empty), Display = ByteText.ToDisplay(f.Full), IsCaptured = f.Sequence == capturedSeq,
            }), ct).ConfigureAwait(false);
        }
        foreach (var img in images)
        {
            var folder = Path.Combine(paths.EvidenceDirectory, m.CapturedUtc.ToString("yyyy", CultureInfo.InvariantCulture), m.CapturedUtc.ToString("MM", CultureInfo.InvariantCulture), t.Uid.ToString("N"));
            Directory.CreateDirectory(folder);
            var ext = img.ContentType == "image/png" ? ".png" : ".jpg";
            var file = Path.Combine(folder, $"{m.Uid:N}_{Sanitize(img.CameraId)}{ext}");
            await File.WriteAllBytesAsync(file, img.Data, ct).ConfigureAwait(false);
            saved.Add(file);
            await TransactionStore.AddEvidenceAsync(s, new EvidenceEntity
            {
                TransactionId = t.Id, MeasurementId = m.Id, CameraId = img.CameraId, CameraName = img.CameraName,
                FilePath = Path.GetRelativePath(paths.EvidenceDirectory, file).Replace('\\', '/'), Sha256 = Convert.ToHexString(SHA256.HashData(img.Data)).ToLowerInvariant(),
                ContentType = img.ContentType, SizeBytes = img.Data.Length, CapturedUtc = img.CapturedAt,
            }, ct).ConfigureAwait(false);
        }
    }

    private static void DeleteFiles(List<string> files)
    {
        foreach (var f in files)
        {
            try { File.Delete(f); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RequireReason(string? reason, ApprovalSettings approvals)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < approvals.MinimumReasonLength) throw new ArgumentException($"A reason of at least {approvals.MinimumReasonLength} characters is required.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    private static string Format(long grams) => WeightDisplayOptions.DefaultKg.Format(Weight.FromGrams(grams));
}

public sealed class ApprovalRequiredException(string message) : Exception(message);
