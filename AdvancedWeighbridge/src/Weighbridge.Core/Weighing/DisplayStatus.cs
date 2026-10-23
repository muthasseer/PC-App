using Weighbridge.Core.Indicators;

namespace Weighbridge.Core.Weighing;

/// <summary>Colour states of the large weight display. Colour is always paired with text and an icon.</summary>
public enum DisplayColor
{
    /// <summary>Stable weight and valid capture conditions.</summary>
    Green,

    /// <summary>Unstable weight or waiting for stability.</summary>
    Yellow,

    /// <summary>Overload, invalid reading, communication error or safety fault.</summary>
    Red,

    /// <summary>Weight captured / transaction completed.</summary>
    Blue,

    /// <summary>Indicator disconnected or no data.</summary>
    Grey,
}

public enum DisplayCondition
{
    Disconnected,
    NoData,
    Stale,
    Invalid,
    Overload,
    Underload,
    IndicatorError,
    Negative,
    Zero,
    BelowMinimum,
    Unstable,
    Stable,
    Captured,
    ManualMode,
}

public sealed record DisplayStatus(DisplayColor Color, DisplayCondition Condition, string TextKey, string Detail)
{
    /// <summary>Segoe MDL2 / Fluent icon glyph name used by the UI (resolved in the view layer).</summary>
    public string Icon => Color switch
    {
        DisplayColor.Green => "check",
        DisplayColor.Yellow => "hourglass",
        DisplayColor.Red => "error",
        DisplayColor.Blue => "lock",
        _ => "plug",
    };
}

public static class DisplayStatusEvaluator
{
    /// <param name="capturedRecently">True right after a capture (shows blue for a short time).</param>
    public static DisplayStatus Evaluate(IndicatorSnapshot snapshot, DateTimeOffset now, CaptureLimits limits, OperatingMode mode, bool capturedRecently, decimal zeroBandKg = 20m)
    {
        if (capturedRecently) return new(DisplayColor.Blue, DisplayCondition.Captured, "status.captured", "Weight captured and locked.");

        if (snapshot.Connection is ConnectionState.Disconnected or ConnectionState.Disabled or ConnectionState.Connecting)
        {
            return mode == OperatingMode.Manual
                ? new(DisplayColor.Grey, DisplayCondition.ManualMode, "status.manualMode", "Manual mode – no indicator connected.")
                : new(DisplayColor.Grey, DisplayCondition.Disconnected, "status.disconnected", "Indicator disconnected.");
        }
        if (snapshot.Connection is ConnectionState.Error) return new(DisplayColor.Red, DisplayCondition.Disconnected, "status.commError", snapshot.LastError ?? "Communication error.");
        if (snapshot.Connection is ConnectionState.Silent || snapshot.Latest is null)
        {
            return snapshot.NewestFrameInvalid
                ? new(DisplayColor.Red, DisplayCondition.Invalid, "status.invalid", snapshot.LastRejectReason ?? "Invalid data.")
                : new(DisplayColor.Grey, DisplayCondition.NoData, "status.noData", "No data from the indicator.");
        }

        var r = snapshot.Latest;
        if ((now - r.ReceivedAt).TotalMilliseconds > limits.FreshnessMs) return new(DisplayColor.Red, DisplayCondition.Stale, "status.stale", "Reading is out of date.");
        if (snapshot.NewestFrameInvalid) return new(DisplayColor.Red, DisplayCondition.Invalid, "status.invalid", snapshot.LastRejectReason ?? "Invalid data.");
        if ((r.Flags & ReadingFlags.Overload) != 0) return new(DisplayColor.Red, DisplayCondition.Overload, "status.overload", "Overload.");
        if ((r.Flags & ReadingFlags.Underload) != 0) return new(DisplayColor.Red, DisplayCondition.Underload, "status.underload", "Under range.");
        if (r.IsError || r.Weight is null) return new(DisplayColor.Red, DisplayCondition.IndicatorError, "status.indicatorError", r.ErrorCode ?? "Indicator error.");

        var kg = r.Weight.Value.Kilograms;
        if (kg > limits.CapacityKg) return new(DisplayColor.Red, DisplayCondition.Overload, "status.overCapacity", "Above weighbridge capacity.");
        if (kg < -zeroBandKg) return new(DisplayColor.Red, DisplayCondition.Negative, "status.negative", "Negative weight – check zero.");
        if (Math.Abs(kg) <= zeroBandKg) return new(DisplayColor.Grey, DisplayCondition.Zero, "status.zero", "Platform empty.");
        if (!snapshot.Stability.IsStable) return new(DisplayColor.Yellow, DisplayCondition.Unstable, "status.unstable", CaptureValidator.StabilityText(snapshot.Stability));
        if (kg < limits.MinimumKg) return new(DisplayColor.Yellow, DisplayCondition.BelowMinimum, "status.belowMinimum", "Below minimum capture weight.");
        return new(DisplayColor.Green, DisplayCondition.Stable, "status.stable", "Stable – ready to capture.");
    }
}
