namespace Weighbridge.Core.Weighing;

public enum OperatingMode
{
    Automatic = 0,
    SemiAutomatic = 1,
    Manual = 2,
}

public enum TransactionStatus
{
    /// <summary>First weight captured, waiting for the second weighing.</summary>
    Pending = 0,
    Completed = 1,
    Cancelled = 2,
}

public enum TransactionDirection
{
    /// <summary>Material coming in (purchase / receipt). The vehicle normally enters loaded.</summary>
    Inbound = 0,

    /// <summary>Material going out (sale / dispatch). The vehicle normally enters empty.</summary>
    Outbound = 1,

    Internal = 2,
}

public enum MeasurementSource
{
    /// <summary>Captured from a connected indicator; raw frame stored.</summary>
    Indicator = 0,

    /// <summary>Typed in by an operator. Never presented as a verified measurement.</summary>
    Manual = 1,

    /// <summary>The approved stored tare of the vehicle.</summary>
    StoredTare = 2,
}

public enum TareMethod
{
    /// <summary>Tare measured on the weighbridge (two weighings).</summary>
    Measured = 0,

    /// <summary>Approved, unexpired stored tare of the vehicle (single weighing).</summary>
    StoredTare = 1,

    /// <summary>Tare typed in by an authorised user.</summary>
    ManualTare = 2,
}

public enum NetAssignmentRule
{
    /// <summary>The heavier weighing is gross. Handles vehicles entering loaded or empty automatically.</summary>
    HeavierIsGross = 0,

    /// <summary>Inbound: first = gross; outbound: first = tare. A contradiction is reported as an error.</summary>
    ByDirection = 1,
}

/// <summary>Which weighing a capture belongs to.</summary>
public enum WeighingStage
{
    First = 1,
    Second = 2,
}
