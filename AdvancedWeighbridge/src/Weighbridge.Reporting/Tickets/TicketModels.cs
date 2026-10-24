using Weighbridge.Core.Units;

namespace Weighbridge.Reporting.Tickets;

public enum PaperSize
{
    A4 = 0,
    A5 = 1,

    /// <summary>80 mm thermal roll (72 mm printable).</summary>
    Thermal80 = 2,

    /// <summary>58 mm thermal roll (48 mm printable).</summary>
    Thermal58 = 3,
}

public sealed record CompanyInfo
{
    public string Name { get; init; } = "Company name";
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? TaxNo { get; init; }

    /// <summary>PNG or JPEG logo.</summary>
    public byte[]? Logo { get; init; }
}

public sealed record TicketWeighing(Weight Weight, DateTimeOffset At, string SourceLabel, bool IsManual, bool IsStable);

public sealed record TicketData
{
    public CompanyInfo Company { get; init; } = new();
    public required string TicketNo { get; init; }
    public Guid TransactionUid { get; init; }
    public TicketStatus Status { get; init; } = TicketStatus.Completed;
    public string? SiteName { get; init; }
    public string? WeighbridgeName { get; init; }
    public required string VehicleReg { get; init; }
    public string? DriverName { get; init; }
    public string? TransportCompany { get; init; }
    public string? Customer { get; init; }
    public string? Supplier { get; init; }
    public string? Material { get; init; }
    public string? OrderNo { get; init; }
    public string? DeliveryNote { get; init; }
    public string? Reference { get; init; }
    public string? Direction { get; init; }
    public string? Remarks { get; init; }
    public TicketWeighing? First { get; init; }
    public TicketWeighing? Second { get; init; }
    public Weight? Gross { get; init; }
    public Weight? Tare { get; init; }
    public Weight? Net { get; init; }
    public string? TareMethodLabel { get; init; }
    public string? OperatorName { get; init; }
    public string? ModeLabel { get; init; }
    public bool HasManualWeight { get; init; }
    public bool HasOverride { get; init; }
    public string? CancelReason { get; init; }
    public int ReprintNumber { get; init; }
    public DateTimeOffset PrintedAt { get; init; }

    /// <summary>Text encoded in the verification QR code.</summary>
    public string? VerificationPayload { get; init; }

    /// <summary>Camera evidence images (JPEG/PNG), shown as thumbnails on A4/A5 tickets.</summary>
    public IReadOnlyList<byte[]> Images { get; init; } = [];
}

public enum TicketStatus { Completed, Pending, Cancelled }

public sealed record TicketTemplate
{
    public string Name { get; init; } = "Standard";
    public PaperSize Paper { get; init; } = PaperSize.A5;

    /// <summary>"en", "si" or "ta".</summary>
    public string Language { get; init; } = "en";

    /// <summary>Show English labels next to the local-language labels.</summary>
    public bool Bilingual { get; init; } = true;

    public bool ShowLogo { get; init; } = true;
    public bool ShowQrCode { get; init; } = true;
    public bool ShowImages { get; init; } = true;
    public bool ShowSignatures { get; init; } = true;
    public int Copies { get; init; } = 1;
    public string? Footer { get; init; } = "Thank you.";
    public WeightDisplayOptions Display { get; init; } = WeightDisplayOptions.DefaultKg;
    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>IANA or Windows time-zone id used for printed times; empty = local time of the machine.</summary>
    public string? TimeZoneId { get; init; }
}
