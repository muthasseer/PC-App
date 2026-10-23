using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weighbridge.Contracts;

/// <summary>
/// Wire contracts of the AWMS cloud API (version 1). Weights are integer grams, times are UTC ISO-8601.
/// Changing a contract requires a new API version; the Android client mirrors these types.
/// </summary>
public static class Api
{
    public const string Version = "v1";
    public const string Prefix = "/api/v1";
    public const string SiteKeyHeader = "X-AWMS-Site-Key";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

// ----------------------------------------------------------------------- site → cloud synchronisation

public sealed record SyncMeasurementDto(
    Guid Uid,
    int Stage,
    string Source,
    long WeightG,
    string? ReportedValue,
    string ReportedUnit,
    bool IsStable,
    bool UnstableOverride,
    string? OverrideReason,
    string? ManualReason,
    DateTimeOffset CapturedUtc,
    int? ReadingAgeMs,
    string? RawText,
    string? ProfileName,
    int? ProfileVersion,
    string OperatingMode,
    string OperatorName,
    Guid? CorrectsMeasurementUid,
    string? CorrectionReason);

public sealed record SyncTransactionDto(
    Guid Uid,
    string TicketNo,
    int Version,
    string Status,
    string Direction,
    string OperatingMode,
    string SiteCode,
    string WeighbridgeCode,
    string VehicleReg,
    string? DriverName,
    string? CustomerCode,
    string? CustomerName,
    string? SupplierCode,
    string? SupplierName,
    string? MaterialCode,
    string? MaterialName,
    string? OrderNo,
    string? DeliveryNoteNo,
    string? ReferenceNo,
    string? Remarks,
    long? FirstG,
    long? SecondG,
    long? GrossG,
    long? TareG,
    long? NetG,
    bool? FirstIsGross,
    string? TareMethod,
    bool HasManualWeight,
    bool HasOverride,
    string CreatedBy,
    DateTimeOffset CreatedUtc,
    string? CompletedBy,
    DateTimeOffset? CompletedUtc,
    string? CancelledBy,
    DateTimeOffset? CancelledUtc,
    string? CancelReason,
    DateTimeOffset UpdatedUtc,
    string? ReplacesTicketNo,
    IReadOnlyList<SyncMeasurementDto> Measurements);

public sealed record SyncAuditEventDto(
    Guid Uid,
    long LocalId,
    DateTimeOffset AtUtc,
    string Username,
    string? Machine,
    string Category,
    string Action,
    string? EntityType,
    string? EntityId,
    string Summary,
    string? Reason,
    string PrevHash,
    string Hash);

public sealed record SyncNotificationDto(Guid Uid, DateTimeOffset AtUtc, string Severity, string Category, string Title, string Message, string? EntityType, string? EntityId);

/// <summary>Directory of site users for central reporting. Never contains passwords or password hashes.</summary>
public sealed record SiteUserDto(string Username, string DisplayName, string Role, bool IsActive, DateTimeOffset? LastLoginUtc);

public sealed record SyncBatch<T>(IReadOnlyList<T> Items);

public enum SyncItemStatus
{
    /// <summary>Stored (new or newer version).</summary>
    Accepted,

    /// <summary>The server already has exactly this version.</summary>
    Unchanged,

    /// <summary>The server has a newer version; the item was ignored.</summary>
    Stale,

    /// <summary>Same version but different content; recorded as a conflict for review.</summary>
    Conflict,

    /// <summary>Invalid payload.</summary>
    Rejected,
}

public sealed record SyncItemResult(string Uid, SyncItemStatus Status, int? ServerVersion, string? Message);

public sealed record SyncBatchResult(IReadOnlyList<SyncItemResult> Results);

public sealed record WeighbridgeStatusDto(
    string Code,
    string Name,
    string Connection,
    long? LiveG,
    bool? Stable,
    DateTimeOffset? MeasuredUtc,
    string? DisplayStatus,
    string OperatingMode,
    string? AutomationState,
    string? LastError,
    int PendingTransactions);

public sealed record SystemHealthDto(
    double DiskFreeGb,
    DateTimeOffset? LastBackupUtc,
    long SyncPending,
    long SyncFailed,
    string DatabaseProvider,
    IReadOnlyList<string> Warnings);

public sealed record HeartbeatRequest(
    string SiteCode,
    string SiteName,
    string AppVersion,
    DateTimeOffset SentUtc,
    IReadOnlyList<WeighbridgeStatusDto> Weighbridges,
    SystemHealthDto Health);

public sealed record HeartbeatResponse(DateTimeOffset ServerUtc, int PendingCommands);

// ----------------------------------------------------------------------- cloud → site commands

public static class CommandTypes
{
    public const string CancelTransaction = "CancelTransaction";
    public const string ApproveRequest = "ApproveRequest";
    public const string RejectRequest = "RejectRequest";
}

public sealed record RemoteCommandDto(long Id, Guid Uid, string Type, string? TargetUid, string? Reason, string RequestedBy, DateTimeOffset RequestedUtc, string Status, string? ResultMessage);

public sealed record CommandAck(bool Success, string? Message);

// ----------------------------------------------------------------------- web / mobile API

public sealed record LoginRequest(string Username, string Password, string? TotpCode);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresUtc, string RefreshToken, UserInfoDto User);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UserInfoDto(long Id, string Username, string DisplayName, string Role, IReadOnlyList<string> Permissions, IReadOnlyList<string> Sites, bool MfaEnabled, bool MustChangePassword = false, bool MfaSetupRequired = false);

public sealed record SiteDto(long Id, string Code, string Name, DateTimeOffset? LastHeartbeatUtc, bool Online, string? AppVersion);

public sealed record SiteStatusDto(SiteDto Site, IReadOnlyList<WeighbridgeStatusDto> Weighbridges, SystemHealthDto? Health, double? HeartbeatAgeSeconds, bool LiveDataStale);

public sealed record TransactionSummaryDto(
    Guid Uid,
    string TicketNo,
    string SiteCode,
    string Status,
    string VehicleReg,
    string? Customer,
    string? Supplier,
    string? Material,
    long? GrossG,
    long? TareG,
    long? NetG,
    bool HasManualWeight,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    string OperatingMode);

public sealed record TransactionDetailDto(TransactionSummaryDto Summary, SyncTransactionDto Transaction, IReadOnlyList<RemoteCommandDto> Commands);

public sealed record PagedDto<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);

public sealed record SummaryRowDto(string Key, long Count, long NetG);

public sealed record SummaryReportDto(string GroupBy, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<SummaryRowDto> Rows, long TotalCount, long TotalNetG);

public sealed record NotificationDto(long Id, string SiteCode, DateTimeOffset AtUtc, string Severity, string Category, string Title, string Message, string? EntityId, bool IsRead);

public sealed record CancelRequest(string Reason);

public sealed record CreateUserRequest(string Username, string DisplayName, string Password, string Role, IReadOnlyList<string> Sites);

public sealed record CreateSiteRequest(string Code, string Name);

public sealed record CreateSiteResponse(SiteDto Site, string SiteKey);

public sealed record AuditEventDto(long Id, string SiteCode, DateTimeOffset AtUtc, string Username, string Category, string Action, string? EntityType, string? EntityId, string Summary, string? Reason);

/// <summary>Error body of every failed API call (HTTP 4xx/5xx).</summary>
public sealed record ErrorDto(string Error, bool? MfaRequired = null);

public sealed record VersionDto(string Product, string Version, string ApiVersion, DateTimeOffset ServerUtc);

public sealed record MfaSetupResponse(string Secret, string ProvisioningUri, string QrPngBase64);

public sealed record MfaEnableRequest(string Secret, string Code);

public sealed record MfaDisableRequest(string Password, string? Code);

public sealed record LogoutRequest(string RefreshToken);

public sealed record MarkReadRequest(IReadOnlyList<long> Ids);

public sealed record UpdateUserRequest(string? DisplayName, string? Role, IReadOnlyList<string>? Sites, bool? IsActive);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record CloudUserDto(long Id, string Username, string DisplayName, string Role, bool IsActive, bool MfaEnabled, bool Locked, bool MustChangePassword, DateTimeOffset? LastLoginUtc, IReadOnlyList<string> Sites);

public sealed record AdminSiteDto(long Id, string Code, string Name, bool IsActive, string KeyPrefix, DateTimeOffset CreatedUtc, DateTimeOffset? LastHeartbeatUtc, bool Online, string? AppVersion);

public sealed record SiteUserListDto(string SiteCode, string Username, string DisplayName, string Role, bool IsActive, DateTimeOffset? LastLoginUtc);

public sealed record ConflictDto(long Id, string SiteCode, string EntityType, string Uid, int? LocalVersion, int? ServerVersion, DateTimeOffset AtUtc);

/// <summary>
/// Result of the cloud-side continuity check of a site's audit trail: every event must reference the hash of the event
/// before it. A missing, re-ordered or replaced event breaks the chain. (Full content hashes are verified at the site,
/// which also holds the before/after values that are not uploaded.)
/// </summary>
public sealed record AuditContinuityDto(string SiteCode, long Checked, bool Valid, long? FirstLocalId, long? LastLocalId, long? BrokenAtLocalId, string Message);

public sealed record TicketVerifyDto(bool Valid, string Message, string? TicketNo, string? SiteCode, string? Status, string? VehicleReg, long? NetG, DateTimeOffset? CompletedUtc);
