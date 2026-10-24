namespace Weighbridge.Cloud.Services;

// Database rows of the cloud schema (snake_case columns are mapped by Dapper).

public sealed class SiteRow
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
    public string? AppVersion { get; set; }
    public string? HeartbeatJson { get; set; }
    public bool OfflineNotified { get; set; }
}

public sealed class CloudUserRow
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public string? MfaSecret { get; set; }
    public long? MfaLastStep { get; set; }
    public long FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class RefreshTokenRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
    public bool Replaced { get; set; }
}

public sealed class CloudTransactionRow
{
    public long Id { get; set; }
    public long SiteId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string Uid { get; set; } = string.Empty;
    public string TicketNo { get; set; } = string.Empty;
    public long Version { get; set; }
    public string Status { get; set; } = string.Empty;
    public string VehicleReg { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string? SupplierName { get; set; }
    public string? MaterialName { get; set; }
    public long? GrossG { get; set; }
    public long? TareG { get; set; }
    public long? NetG { get; set; }
    public bool HasManual { get; set; }
    public string OperatingMode { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
}

public sealed class CommandRow
{
    public long Id { get; set; }
    public string Uid { get; set; } = string.Empty;
    public long SiteId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? TargetUid { get; set; }
    public string? Reason { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset RequestedUtc { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ResultMessage { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public Contracts.RemoteCommandDto ToDto() => new(Id, Guid.Parse(Uid), Type, TargetUid, Reason, RequestedBy, RequestedUtc, Status, ResultMessage);
}

public sealed class AuditRow
{
    public long Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public long LocalId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

public sealed class NotificationRow
{
    public long Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public DateTimeOffset AtUtc { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public bool IsRead { get; set; }
}

public sealed class ExistingTransactionRow
{
    public long Id { get; set; }
    public long SiteId { get; set; }
    public long Version { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class ExistingAuditRow
{
    public long SiteId { get; set; }
    public string Hash { get; set; } = string.Empty;
}

public sealed class ConflictRow
{
    public long Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string Uid { get; set; } = string.Empty;
    public long? LocalVersion { get; set; }
    public long? ServerVersion { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class SiteUserRow
{
    public string SiteCode { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
}

public sealed class ServerAuditRow
{
    public long Id { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? Ip { get; set; }
}
