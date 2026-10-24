using Weighbridge.Core.Automation;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Devices.IO;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Application.Settings;

public sealed record CompanySettings
{
    public string Name { get; init; } = "My Company";
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? TaxNo { get; init; }

    /// <summary>Logo stored as base64 PNG/JPEG (small).</summary>
    public string? LogoBase64 { get; init; }
}

public sealed record LocalizationSettings
{
    /// <summary>"en", "si" or "ta".</summary>
    public string Language { get; init; } = "en";

    /// <summary>"Light", "Dark" or "System".</summary>
    public string Theme { get; init; } = "Light";

    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public string TimeFormat { get; init; } = "HH:mm:ss";
    public WeightUnit DisplayUnit { get; init; } = WeightUnit.Kilogram;
    public int DisplayDecimals { get; init; }
    public bool ThousandsSeparator { get; init; } = true;
    public string? TimeZoneId { get; init; }

    /// <summary>Size of the live weight digits on the dashboard (device-independent pixels).</summary>
    public double WeightFontSize { get; init; } = 120;

    public string WeightFontFamily { get; init; } = "Bahnschrift SemiBold";

    public WeightDisplayOptions Display => new() { Unit = DisplayUnit, Decimals = DisplayDecimals, UseThousandsSeparator = ThousandsSeparator };
}

public sealed record WeighingSettings
{
    public long ActiveWeighbridgeId { get; init; }
    public OperatingMode DefaultMode { get; init; } = OperatingMode.SemiAutomatic;
    public CapturePolicy Capture { get; init; } = new();
    public int FreshnessMs { get; init; } = 1500;
    public bool AllowNegative { get; init; }
    public NetRules Net { get; init; } = new();
    public TicketNumberOptions TicketNumbers { get; init; } = new();

    /// <summary>Warn when the same vehicle completed a weighing within this many minutes.</summary>
    public int DuplicateWindowMinutes { get; init; } = 15;

    public bool RequireMaterial { get; init; } = true;
    public bool RequireCustomerOrSupplier { get; init; }

    /// <summary>Create vehicles in the master data automatically on their first weighing.</summary>
    public bool AutoCreateVehicles { get; init; } = true;

    /// <summary>In automatic mode, copy customer/supplier/material from the vehicle's previous transaction.</summary>
    public bool AutoFillFromLastTransaction { get; init; } = true;

    /// <summary>Use an approved stored tare automatically when the vehicle has one (single weighing).</summary>
    public bool OfferStoredTare { get; init; } = true;

    public int StoredTareValidityDays { get; init; } = 90;

    public int CameraCaptureTimeoutMs { get; init; } = 2500;
}

public sealed record ApprovalSettings
{
    public bool ManualEntryRequiresSupervisor { get; init; } = true;
    public bool CancelRequiresSupervisor { get; init; } = true;
    public bool UnstableOverrideRequiresSupervisor { get; init; } = true;
    public bool ReprintRequiresReason { get; init; } = true;
    public bool EditCompletedRequiresSupervisor { get; init; } = true;
    public int MinimumReasonLength { get; init; } = 5;
}

public sealed record PrintingSettings
{
    public TicketTemplate Template { get; init; } = new();
    public bool PrintOnFirstWeighing { get; init; }
    public bool PrintOnCompletion { get; init; } = true;

    /// <summary>Ask before printing in semi-automatic mode (operator confirmation).</summary>
    public bool ConfirmBeforePrint { get; init; } = true;

    public string? PrinterName { get; init; }

    /// <summary>Send ESC/POS raster bytes directly to the printer queue (receipt printers).</summary>
    public bool UseEscPosRaw { get; init; }

    public bool SavePdfCopy { get; init; } = true;
}

public sealed record CloudSettings
{
    public bool Enabled { get; init; }
    public string? BaseUrl { get; init; }

    /// <summary>Protected with <see cref="Security.ISecretProtector"/>.</summary>
    public string? SiteKeyProtected { get; init; }

    public int IntervalSeconds { get; init; } = 30;
    public bool SendLiveWeight { get; init; } = true;
    public bool ShareDriverNames { get; init; }
    public bool UploadAudit { get; init; } = true;
    public bool AcceptRemoteCommands { get; init; } = true;

    /// <summary>Only for testing on a private network. Production requires HTTPS.</summary>
    public bool AllowInsecureHttp { get; init; }

    public string? VerifyBaseUrl { get; init; }
}

public sealed record BackupSettings
{
    public bool ScheduleEnabled { get; init; } = true;

    /// <summary>Local time of day, "HH:mm".</summary>
    public string DailyAt { get; init; } = "23:30";

    public string? Directory { get; init; }

    /// <summary>Second copy (USB drive, network share).</summary>
    public string? SecondaryDirectory { get; init; }

    public int KeepCount { get; init; } = 30;
    public bool IncludeEvidence { get; init; } = true;
}

public sealed record SecuritySettings
{
    public int SessionTimeoutMinutes { get; init; } = 30;
    public int MaxFailedLogins { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
    public int PasswordMinLength { get; init; } = 8;
    public bool RequireMfaForAdministrators { get; init; }
}

public sealed record AutomationSettings
{
    public decimal ZeroBandKg { get; init; } = 100m;
    public decimal VehicleDetectKg { get; init; } = 500m;
    public bool RequireIdentification { get; init; } = true;
    public int IdentificationTimeoutMs { get; init; } = 20_000;
    public int StableTimeoutMs { get; init; } = 90_000;
    public bool AutoOpenExitBarrier { get; init; } = true;
    public decimal AbnormalChangeKg { get; init; } = 300m;
    public int CommunicationLossMs { get; init; } = 3000;

    public AutomationOptions ToOptions(OperatingMode mode, CaptureLimits limits, HardwareConfig hardware, PrintingSettings printing) => new()
    {
        Mode = mode,
        ZeroBandKg = ZeroBandKg,
        VehicleDetectKg = VehicleDetectKg,
        UsePresenceSensor = hardware.HasPresenceSensor,
        RequireIdentification = RequireIdentification,
        IdentificationTimeoutMs = IdentificationTimeoutMs,
        UsePositionSensors = hardware.HasPositionSensors,
        StableTimeoutMs = StableTimeoutMs,
        CommunicationLossMs = CommunicationLossMs,
        PrintFirstWeighing = printing.PrintOnFirstWeighing,
        PrintOnCompletion = printing.PrintOnCompletion,
        AutoOpenExitBarrier = AutoOpenExitBarrier,
        HasExitBarrier = hardware.HasExitBarrier,
        AbnormalChangeKg = AbnormalChangeKg,
        Limits = limits,
        Policy = new CapturePolicy { Unstable = UnstableCapturePolicy.Reject },
    };
}

/// <summary>Setting keys and their sections (stored as JSON documents in the settings table).</summary>
public static class SettingKeys
{
    public const string Company = "company";
    public const string Localization = "localization";
    public const string Weighing = "weighing";
    public const string Approvals = "approvals";
    public const string Printing = "printing";
    public const string Cloud = "cloud";
    public const string Backup = "backup";
    public const string Security = "security";
    public const string Automation = "automation";
    public const string Hardware = "hardware";
    public const string TicketSigningKey = "ticket.signingKey";
    public const string CloudAuditCursor = "cloud.auditCursor";
    public const string InstallationId = "installation.id";
}
