using Weighbridge.Core.Indicators;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Data;

// Row models. Weights are stored in grams (exact integers); helper properties expose kilograms.

public sealed class RoleEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<string> Permissions { get; set; } = [];
}

public sealed class UserEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public long RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public string? MfaSecretProtected { get; set; }
    public long? MfaLastStep { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public DateTimeOffset? PasswordChangedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public bool MfaEnabled => !string.IsNullOrEmpty(MfaSecretProtected);
}

public sealed class LoginHistoryEntity
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string? Machine { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class SiteEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class WeighbridgeEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public long SiteId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long CapacityG { get; set; }
    public long MinCaptureG { get; set; }
    public long DivisionG { get; set; } = 10_000;
    public bool IsActive { get; set; } = true;
    public decimal CapacityKg => CapacityG / 1000m;
    public decimal MinCaptureKg => MinCaptureG / 1000m;
}

public sealed class ProfileEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public int Version { get; set; }
    public string ProfileJson { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public IndicatorProfile ToProfile() => IndicatorProfile.FromJson(ProfileJson);
}

public sealed class IndicatorDeviceEntity
{
    public long Id { get; set; }
    public long WeighbridgeId { get; set; }
    public long ProfileId { get; set; }
    public string? PortOverride { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class VehicleCategoryEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class TransportCompanyEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class DriverEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? LicenseNo { get; set; }
    public string? NicNo { get; set; }
    public string? Phone { get; set; }
    public long? TransportCompanyId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public enum PartyKind { Customer, Supplier }

/// <summary>Customer or supplier (identical columns, separate tables).</summary>
public sealed class PartyEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? TaxNo { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class MaterialEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Grade { get; set; }
    public string Unit { get; set; } = "kg";
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class VehicleEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string Registration { get; set; } = string.Empty;
    public string RegistrationNorm { get; set; } = string.Empty;
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? VehicleType { get; set; }
    public string? MakeModel { get; set; }
    public int? Axles { get; set; }
    public long? TransportCompanyId { get; set; }
    public long? DefaultDriverId { get; set; }
    public string? RfidTag { get; set; }
    public string? QrCode { get; set; }
    public long? StoredTareG { get; set; }
    public DateTimeOffset? StoredTareUtc { get; set; }
    public long? StoredTareApprovedBy { get; set; }
    public DateTimeOffset? StoredTareApprovedUtc { get; set; }
    public DateTimeOffset? StoredTareExpiresUtc { get; set; }
    public bool IsBlacklisted { get; set; }
    public string? BlacklistReason { get; set; }
    public string? PhotoPath { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    public Weight? StoredTare => StoredTareG is { } g ? Weight.FromGrams(g) : null;

    /// <summary>An approved stored tare that has not expired may be used for single weighing.</summary>
    public bool HasUsableStoredTare(DateTimeOffset now) =>
        StoredTareG is not null && StoredTareApprovedBy is not null && (StoredTareExpiresUtc is null || StoredTareExpiresUtc > now);
}

public sealed class OrderEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string OrderType { get; set; } = "Sales";
    public string OrderNo { get; set; } = string.Empty;
    public long? CustomerId { get; set; }
    public long? SupplierId { get; set; }
    public long? MaterialId { get; set; }
    public long? OrderedQtyG { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset? ValidFromUtc { get; set; }
    public DateTimeOffset? ValidToUtc { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class TransactionEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string TicketNo { get; set; } = string.Empty;
    public long SiteId { get; set; }
    public long WeighbridgeId { get; set; }
    public TransactionStatus Status { get; set; }
    public TransactionDirection Direction { get; set; }
    public OperatingMode OperatingMode { get; set; }
    public long? VehicleId { get; set; }
    public string VehicleReg { get; set; } = string.Empty;
    public string VehicleRegNorm { get; set; } = string.Empty;
    public long? DriverId { get; set; }
    public string? DriverName { get; set; }
    public long? TransportCompanyId { get; set; }
    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public long? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public long? MaterialId { get; set; }
    public string? MaterialName { get; set; }
    public long? OrderId { get; set; }
    public string? DeliveryNoteNo { get; set; }
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
    public long? ReplacesTransactionId { get; set; }
    public long? FirstMeasurementId { get; set; }
    public long? SecondMeasurementId { get; set; }
    public long? FirstG { get; set; }
    public long? SecondG { get; set; }
    public long? GrossG { get; set; }
    public long? TareG { get; set; }
    public long? NetG { get; set; }
    public bool? FirstIsGross { get; set; }
    public TareMethod? TareMethod { get; set; }
    public bool HasManualWeight { get; set; }
    public bool HasOverride { get; set; }
    public long CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public long? CompletedBy { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public long? CancelledBy { get; set; }
    public DateTimeOffset? CancelledUtc { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public int Version { get; set; } = 1;

    // Filled by joins in search queries.
    public string? SiteCode { get; set; }
    public string? WeighbridgeCode { get; set; }
    public string? ProfileName { get; set; }

    public Weight? First => FirstG is { } g ? Weight.FromGrams(g) : null;
    public Weight? Second => SecondG is { } g ? Weight.FromGrams(g) : null;
    public Weight? Gross => GrossG is { } g ? Weight.FromGrams(g) : null;
    public Weight? Tare => TareG is { } g ? Weight.FromGrams(g) : null;
    public Weight? Net => NetG is { } g ? Weight.FromGrams(g) : null;
}

public sealed class MeasurementEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public long TransactionId { get; set; }
    public int Stage { get; set; }
    public MeasurementSource Source { get; set; }
    public long WeightG { get; set; }
    public string? ReportedValue { get; set; }
    public string ReportedUnit { get; set; } = "kg";
    public int ReportedDecimals { get; set; }
    public bool IsStable { get; set; }
    public string? StabilityReason { get; set; }
    public long? StabilitySpanG { get; set; }
    public int? StableForMs { get; set; }
    public bool UnstableOverride { get; set; }
    public string? OverrideReason { get; set; }
    public long? OverrideApprovedBy { get; set; }
    public string? ManualReason { get; set; }
    public long? ManualApprovedBy { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
    public DateTimeOffset? ReadingReceivedUtc { get; set; }
    public int? ReadingAgeMs { get; set; }
    public string? RawText { get; set; }
    public string? RawHex { get; set; }
    public int IndicatorFlags { get; set; }
    public string? PortDescription { get; set; }
    public string? ProfileUid { get; set; }
    public string? ProfileName { get; set; }
    public int? ProfileVersion { get; set; }
    public OperatingMode OperatingMode { get; set; }
    public long OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string? Machine { get; set; }
    public long? CorrectsMeasurementId { get; set; }
    public string? CorrectionReason { get; set; }

    public Weight Weight => Weight.FromGrams(WeightG);
}

public sealed class RawFrameEntity
{
    public long Id { get; set; }
    public long MeasurementId { get; set; }
    public long Seq { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public string Hex { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
    public bool IsCaptured { get; set; }
}

public sealed class EvidenceEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public long TransactionId { get; set; }
    public long? MeasurementId { get; set; }
    public string CameraId { get; set; } = string.Empty;
    public string CameraName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
    public long SizeBytes { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
}

public sealed class TransactionChangeEntity
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }
    public long UserId { get; set; }
    public string? UserName { get; set; }
    public long? ApprovedBy { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class PrintHistoryEntity
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public string Action { get; set; } = "Print";
    public int Copies { get; set; } = 1;
    public string? Printer { get; set; }
    public string? Template { get; set; }
    public string? Reason { get; set; }
    public long UserId { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class TicketTemplateEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Paper { get; set; } = "A5";
    public string TemplateJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class ApprovalEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public string RequestType { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public long RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public string Status { get; set; } = "Pending";
    public long? DecidedBy { get; set; }
    public string? DecidedByName { get; set; }
    public DateTimeOffset? DecidedUtc { get; set; }
    public string? DecisionReason { get; set; }
    public string? PayloadJson { get; set; }
    public string Source { get; set; } = "Local";
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public long? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Machine { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? OldJson { get; set; }
    public string? NewJson { get; set; }
    public string? Reason { get; set; }
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

public sealed class NotificationEntity
{
    public long Id { get; set; }
    public Guid Uid { get; set; } = Guid.NewGuid();
    public DateTimeOffset AtUtc { get; set; }
    public string Severity { get; set; } = "Info";
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public bool IsRead { get; set; }
}

public sealed class DeviceEventEntity
{
    public long Id { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public long? WeighbridgeId { get; set; }
    public string Device { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class BackupHistoryEntity
{
    public long Id { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string Kind { get; set; } = "Manual";
    public string? FilePath { get; set; }
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string Status { get; set; } = "Running";
    public string? Error { get; set; }
    public long? InitiatedBy { get; set; }
}

public sealed class SyncQueueEntity
{
    public long Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityUid { get; set; } = string.Empty;
    public int EntityVersion { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset? SentUtc { get; set; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
}
