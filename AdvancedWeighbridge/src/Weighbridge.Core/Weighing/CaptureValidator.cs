using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Weighing;

public sealed record CaptureLimits
{
    /// <summary>Captures below this are refused (empty deck, person on the platform).</summary>
    public decimal MinimumKg { get; init; } = 200m;

    /// <summary>Weighbridge rated capacity. Above it the reading is an overload.</summary>
    public decimal CapacityKg { get; init; } = 100_000m;

    public bool AllowNegative { get; init; }

    /// <summary>The latest reading must be younger than this when GET WEIGHT is pressed.</summary>
    public int FreshnessMs { get; init; } = 1500;
}

public enum UnstableCapturePolicy
{
    /// <summary>Refuse; the operator presses GET WEIGHT again later.</summary>
    Reject = 0,

    /// <summary>Wait (up to a timeout) until the weight is stable, then capture automatically.</summary>
    WaitForStable = 1,

    /// <summary>Allow capturing an unstable weight only with supervisor authorisation and a reason (audited).</summary>
    SupervisorOverride = 2,
}

public sealed record CapturePolicy
{
    public UnstableCapturePolicy Unstable { get; init; } = UnstableCapturePolicy.WaitForStable;
    public int WaitForStableTimeoutMs { get; init; } = 15000;

    /// <summary>Number of frames before/after the captured one kept as evidence with the measurement.</summary>
    public int EvidenceFrameCount { get; init; } = 10;
}

public enum CaptureIssueCode
{
    NotConnected,
    NoReading,
    Stale,
    LatestFrameInvalid,
    Overload,
    IndicatorError,
    Negative,
    BelowMinimum,
    AboveCapacity,
    Unstable,
}

public sealed record CaptureIssue(CaptureIssueCode Code, string Message);

public enum CaptureVerdict
{
    Allowed = 0,

    /// <summary>Everything is valid except stability; the policy says wait.</summary>
    WaitForStability = 1,

    /// <summary>Everything is valid except stability; a supervisor may authorise an unstable capture.</summary>
    OverrideRequired = 2,

    Rejected = 3,
}

public sealed record CaptureDecision(CaptureVerdict Verdict, IReadOnlyList<CaptureIssue> Issues, WeightReading? Reading, StabilityState Stability)
{
    public bool IsAllowed => Verdict == CaptureVerdict.Allowed;
    public string Summary => Issues.Count == 0 ? "OK" : string.Join(" ", Issues.Select(i => i.Message));
}

/// <summary>
/// Decides whether the latest reading may become a measurement. Used identically by GET WEIGHT (semi-automatic)
/// and by the automation engine, so both modes apply the same safety checks.
/// </summary>
public static class CaptureValidator
{
    public static CaptureDecision Evaluate(IndicatorSnapshot snapshot, DateTimeOffset now, CaptureLimits limits, CapturePolicy policy)
    {
        var issues = new List<CaptureIssue>();
        var reading = snapshot.Latest;
        var stability = snapshot.Stability;

        if (snapshot.Connection is not ConnectionState.Connected)
        {
            issues.Add(new(CaptureIssueCode.NotConnected, $"Indicator is {snapshot.Connection.ToString().ToLowerInvariant()}."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }
        if (reading is null)
        {
            issues.Add(new(CaptureIssueCode.NoReading, "No valid reading has been received from the indicator."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, null, stability);
        }

        var age = (now - reading.ReceivedAt).TotalMilliseconds;
        if (age > limits.FreshnessMs || age < -1000)
        {
            issues.Add(new(CaptureIssueCode.Stale, $"The latest reading is {Math.Max(0, age) / 1000:0.0} s old (limit {limits.FreshnessMs / 1000.0:0.0} s)."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }
        if (snapshot.NewestFrameInvalid)
        {
            issues.Add(new(CaptureIssueCode.LatestFrameInvalid, "The newest frame from the indicator was invalid: " + snapshot.LastRejectReason));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }
        if (reading.IsOverload)
        {
            issues.Add(new(CaptureIssueCode.Overload, "The indicator reports overload / out of range."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }
        if (reading.IsError || reading.Weight is null)
        {
            issues.Add(new(CaptureIssueCode.IndicatorError, "The indicator reports an error" + (reading.ErrorCode is null ? "." : $" ({reading.ErrorCode}).")));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }

        var kg = reading.Weight.Value.Kilograms;
        if (kg < 0 && !limits.AllowNegative)
        {
            issues.Add(new(CaptureIssueCode.Negative, "The weight is negative."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }
        if (Math.Abs(kg) < limits.MinimumKg)
        {
            issues.Add(new(CaptureIssueCode.BelowMinimum, $"{kg:0.###} kg is below the minimum capture weight of {limits.MinimumKg:0.###} kg."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }
        if (kg > limits.CapacityKg)
        {
            issues.Add(new(CaptureIssueCode.AboveCapacity, $"{kg:0.###} kg exceeds the weighbridge capacity of {limits.CapacityKg:0.###} kg."));
            return new CaptureDecision(CaptureVerdict.Rejected, issues, reading, stability);
        }

        if (!stability.IsStable)
        {
            issues.Add(new(CaptureIssueCode.Unstable, "The weight is not stable (" + StabilityText(stability) + ")."));
            var verdict = policy.Unstable switch
            {
                UnstableCapturePolicy.WaitForStable => CaptureVerdict.WaitForStability,
                UnstableCapturePolicy.SupervisorOverride => CaptureVerdict.OverrideRequired,
                _ => CaptureVerdict.Rejected,
            };
            return new CaptureDecision(verdict, issues, reading, stability);
        }

        return new CaptureDecision(CaptureVerdict.Allowed, issues, reading, stability);
    }

    public static string StabilityText(StabilityState s) => s.Reason switch
    {
        StabilityReason.Stable => "stable",
        StabilityReason.Settling => $"settling, {s.StableForMs / 1000.0:0.0} s",
        StabilityReason.OutsideTolerance => $"moving, spread {s.SpanKg:0.###} kg",
        StabilityReason.TooFewSamples => "too few readings",
        StabilityReason.IndicatorMotion => "indicator reports motion",
        StabilityReason.IndicatorNoFlag => "indicator does not report stability",
        StabilityReason.DataGap => "data gap",
        StabilityReason.Fault => "indicator fault",
        _ => "no data",
    };
}

/// <summary>Everything stored with a measurement so that it can be audited later.</summary>
public sealed record CapturedWeight
{
    public required Weight Weight { get; init; }
    public decimal? ReportedValue { get; init; }
    public WeightUnit ReportedUnit { get; init; } = WeightUnit.Kilogram;
    public int ReportedDecimals { get; init; }
    public required MeasurementSource Source { get; init; }
    public bool IsStable { get; init; }
    public StabilityReason StabilityReason { get; init; }
    public decimal StabilitySpanKg { get; init; }
    public int StableForMs { get; init; }
    public bool UnstableOverride { get; init; }
    public string? OverrideReason { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? ReadingReceivedAt { get; init; }
    public int? ReadingAgeMs { get; init; }
    public string? RawText { get; init; }
    public string? RawHex { get; init; }
    public IReadOnlyList<RawFrame> EvidenceFrames { get; init; } = [];
    public ReadingFlags Flags { get; init; }
    public string? PortDescription { get; init; }
    public Guid? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public int? ProfileVersion { get; init; }
    public required OperatingMode Mode { get; init; }

    /// <summary>Mandatory for manual entries.</summary>
    public string? ManualReason { get; init; }

    public static CapturedWeight FromDecision(CaptureDecision decision, IndicatorSnapshot snapshot, DateTimeOffset now, OperatingMode mode,
        IReadOnlyList<RawFrame> evidence, bool unstableOverride = false, string? overrideReason = null)
    {
        var r = decision.Reading ?? throw new InvalidOperationException("No reading to capture.");
        return new CapturedWeight
        {
            Weight = r.Weight ?? throw new InvalidOperationException("The reading has no weight."),
            ReportedValue = r.Value,
            ReportedUnit = r.Unit,
            ReportedDecimals = r.ReportedDecimals,
            Source = MeasurementSource.Indicator,
            IsStable = decision.Stability.IsStable,
            StabilityReason = decision.Stability.Reason,
            StabilitySpanKg = decision.Stability.SpanKg,
            StableForMs = decision.Stability.StableForMs,
            UnstableOverride = unstableOverride,
            OverrideReason = overrideReason,
            CapturedAt = now,
            ReadingReceivedAt = r.ReceivedAt,
            ReadingAgeMs = (int)Math.Max(0, (now - r.ReceivedAt).TotalMilliseconds),
            RawText = r.Text,
            RawHex = r.Frame?.Hex,
            EvidenceFrames = evidence,
            Flags = r.Flags,
            PortDescription = snapshot.PortDescription,
            ProfileId = snapshot.ProfileId,
            ProfileName = snapshot.ProfileName,
            ProfileVersion = snapshot.ProfileVersion,
            Mode = mode,
        };
    }
}
