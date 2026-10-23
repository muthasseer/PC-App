namespace Weighbridge.Core.Security;

/// <summary>Fine-grained permissions. Stored as strings so that the database and the cloud share them.</summary>
public static class Permissions
{
    public const string DashboardView = "dashboard.view";
    public const string WeighingCapture = "weighing.capture";
    public const string WeighingAutomatic = "weighing.automatic";
    public const string WeighingManualEntry = "weighing.manual_entry";
    public const string WeighingOverrideUnstable = "weighing.override_unstable";
    public const string ApprovalsDecide = "approvals.decide";
    public const string TransactionEdit = "transaction.edit";
    public const string TransactionCorrectWeight = "transaction.correct_weight";
    public const string TransactionCancel = "transaction.cancel";
    public const string TicketPrint = "ticket.print";
    public const string TicketReprint = "ticket.reprint";
    public const string HistoryView = "history.view";
    public const string ReportsView = "reports.view";
    public const string ReportsExport = "reports.export";
    public const string MasterDataView = "masterdata.view";
    public const string MasterDataEdit = "masterdata.edit";
    public const string VehicleTareApprove = "vehicle.tare_approve";
    public const string VehicleBlacklist = "vehicle.blacklist";
    public const string DevicesView = "devices.view";
    public const string DevicesConfigure = "devices.configure";
    public const string HardwareOverride = "hardware.override";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditView = "audit.view";
    public const string BackupManage = "backup.manage";
    public const string CloudManage = "cloud.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        DashboardView, WeighingCapture, WeighingAutomatic, WeighingManualEntry, WeighingOverrideUnstable, ApprovalsDecide,
        TransactionEdit, TransactionCorrectWeight, TransactionCancel, TicketPrint, TicketReprint, HistoryView, ReportsView,
        ReportsExport, MasterDataView, MasterDataEdit, VehicleTareApprove, VehicleBlacklist, DevicesView, DevicesConfigure,
        HardwareOverride, UsersManage, RolesManage, SettingsManage, AuditView, BackupManage, CloudManage,
    ];
}

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Manager = "Manager";
    public const string Operator = "Operator";
    public const string Auditor = "Auditor";
    public const string Viewer = "Viewer";

    public static IReadOnlyList<string> All { get; } = [Administrator, Manager, Operator, Auditor, Viewer];

    /// <summary>Default role → permission mapping installed in a new database (editable by administrators).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Defaults { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [Administrator] = Permissions.All,
        [Manager] =
        [
            Permissions.DashboardView, Permissions.WeighingCapture, Permissions.WeighingAutomatic, Permissions.WeighingManualEntry,
            Permissions.WeighingOverrideUnstable, Permissions.ApprovalsDecide, Permissions.TransactionEdit, Permissions.TransactionCorrectWeight,
            Permissions.TransactionCancel, Permissions.TicketPrint, Permissions.TicketReprint, Permissions.HistoryView, Permissions.ReportsView,
            Permissions.ReportsExport, Permissions.MasterDataView, Permissions.MasterDataEdit, Permissions.VehicleTareApprove,
            Permissions.VehicleBlacklist, Permissions.DevicesView, Permissions.HardwareOverride, Permissions.AuditView, Permissions.BackupManage,
        ],
        [Operator] =
        [
            Permissions.DashboardView, Permissions.WeighingCapture, Permissions.WeighingManualEntry, Permissions.TransactionEdit,
            Permissions.TicketPrint, Permissions.HistoryView, Permissions.ReportsView, Permissions.MasterDataView, Permissions.MasterDataEdit,
            Permissions.DevicesView,
        ],
        [Auditor] =
        [
            Permissions.DashboardView, Permissions.HistoryView, Permissions.ReportsView, Permissions.ReportsExport, Permissions.MasterDataView,
            Permissions.DevicesView, Permissions.AuditView,
        ],
        [Viewer] = [Permissions.DashboardView, Permissions.HistoryView, Permissions.ReportsView],
    };
}

/// <summary>The authenticated user of a session, with the resolved permission set.</summary>
public sealed record UserContext(long UserId, string Username, string DisplayName, string Role, IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);

    public void Demand(string permission)
    {
        if (!Has(permission)) throw new PermissionDeniedException(permission, Username);
    }

    public static UserContext System { get; } = new(0, "system", "System", Roles.Administrator, Security.Permissions.All.ToHashSet());
}

public sealed class PermissionDeniedException(string permission, string username)
    : Exception($"User '{username}' does not have the permission '{permission}'.")
{
    public string Permission { get; } = permission;
}
