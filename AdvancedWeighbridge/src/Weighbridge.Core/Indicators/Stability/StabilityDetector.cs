namespace Weighbridge.Core.Indicators.Stability;

public enum StabilityMode
{
    /// <summary>Stable when readings stayed inside the tolerance band for the whole window (software check only).</summary>
    Software = 0,

    /// <summary>Trust the indicator's own stable/motion flag only. Readings without a flag are treated as unstable.</summary>
    IndicatorFlag = 1,

    /// <summary>Software check AND the indicator must not report motion (recommended).</summary>
    Both = 2,
}

public sealed record StabilityOptions
{
    public StabilityMode Mode { get; init; } = StabilityMode.Both;

    /// <summary>The weight must stay inside the band for at least this long.</summary>
    public int WindowMs { get; init; } = 2000;

    /// <summary>Maximum spread (max − min) in kilograms inside the window. Typically 1 indicator division.</summary>
    public decimal ToleranceKg { get; init; } = 20m;

    /// <summary>Minimum number of readings inside the window.</summary>
    public int MinSamples { get; init; } = 3;

    /// <summary>A gap between readings longer than this restarts the stability window.</summary>
    public int MaxGapMs { get; init; } = 1500;
}

public enum StabilityReason
{
    Stable = 0,
    NoData,
    Settling,
    OutsideTolerance,
    TooFewSamples,
    IndicatorMotion,
    IndicatorNoFlag,
    Fault,
    DataGap,
}

public sealed record StabilityState(
    bool IsStable,
    StabilityReason Reason,
    decimal SpanKg,
    int StableForMs,
    int SampleCount,
    decimal? LatestKg)
{
    public static StabilityState Unknown { get; } = new(false, StabilityReason.NoData, 0, 0, 0, null);
}

/// <summary>
/// Sliding-window stability detection. Feed every parsed reading (including faults) in arrival order.
/// The detector keeps only a few seconds of samples and is O(window) per reading.
/// </summary>
public sealed class StabilityDetector
{
    private readonly StabilityOptions _options;
    private readonly LinkedList<(DateTimeOffset At, decimal Kg)> _samples = new();
    private bool? _lastIndicatorFlag;
    private bool _fault;
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    public StabilityDetector(StabilityOptions options)
    {
        if (options.WindowMs < 0) throw new ArgumentOutOfRangeException(nameof(options), "WindowMs must not be negative.");
        if (options.ToleranceKg < 0) throw new ArgumentOutOfRangeException(nameof(options), "ToleranceKg must not be negative.");
        _options = options;
        Current = StabilityState.Unknown;
    }

    public StabilityOptions Options => _options;
    public StabilityState Current { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        _lastIndicatorFlag = null;
        _fault = false;
        _lastAt = DateTimeOffset.MinValue;
        Current = StabilityState.Unknown;
    }

    public StabilityState Add(WeightReading reading)
    {
        if (_lastAt != DateTimeOffset.MinValue && (reading.ReceivedAt - _lastAt).TotalMilliseconds > _options.MaxGapMs)
        {
            _samples.Clear();
        }
        _lastAt = reading.ReceivedAt;
        _lastIndicatorFlag = reading.IndicatorStable;

        if (!reading.IsUsable || reading.Weight is not { } w)
        {
            _fault = true;
            _samples.Clear();
            Current = new StabilityState(false, StabilityReason.Fault, 0, 0, 0, null);
            return Current;
        }

        _fault = false;
        _samples.AddLast((reading.ReceivedAt, w.Kilograms));

        // Keep a little more than one window so that "stable for" can be measured.
        var keepFrom = reading.ReceivedAt.AddMilliseconds(-(_options.WindowMs * 2 + 1000));
        while (_samples.First is { } first && first.Value.At < keepFrom) _samples.RemoveFirst();

        Current = Evaluate(reading.ReceivedAt);
        return Current;
    }

    /// <summary>Re-evaluates without a new reading, e.g. when the line went silent.</summary>
    public StabilityState Evaluate(DateTimeOffset now)
    {
        if (_fault) return new StabilityState(false, StabilityReason.Fault, 0, 0, 0, null);
        if (_samples.Last is not { } lastNode) return StabilityState.Unknown;

        var latest = lastNode.Value;
        if ((now - latest.At).TotalMilliseconds > _options.MaxGapMs)
        {
            return new StabilityState(false, StabilityReason.DataGap, 0, 0, 0, latest.Kg);
        }

        // Walk backwards while every sample stays inside the tolerance band.
        var min = latest.Kg;
        var max = latest.Kg;
        var bandStart = latest.At;
        var count = 0;
        for (var node = _samples.Last; node is not null; node = node.Previous)
        {
            var kg = node.Value.Kg;
            var newMin = Math.Min(min, kg);
            var newMax = Math.Max(max, kg);
            if (newMax - newMin > _options.ToleranceKg) break;
            min = newMin;
            max = newMax;
            bandStart = node.Value.At;
            count++;
        }

        var stableFor = (int)Math.Max(0, (latest.At - bandStart).TotalMilliseconds);
        var span = max - min;
        var softwareStable = stableFor >= _options.WindowMs && count >= _options.MinSamples;

        StabilityReason reason;
        bool stable;
        switch (_options.Mode)
        {
            case StabilityMode.IndicatorFlag:
                stable = _lastIndicatorFlag == true;
                reason = _lastIndicatorFlag switch { true => StabilityReason.Stable, false => StabilityReason.IndicatorMotion, null => StabilityReason.IndicatorNoFlag };
                break;
            case StabilityMode.Both when _lastIndicatorFlag == false:
                stable = false;
                reason = StabilityReason.IndicatorMotion;
                break;
            default:
                stable = softwareStable;
                reason = softwareStable ? StabilityReason.Stable
                    : count < _options.MinSamples && stableFor >= _options.WindowMs ? StabilityReason.TooFewSamples
                    : count == _samples.Count ? StabilityReason.Settling
                    : StabilityReason.OutsideTolerance;
                break;
        }

        return new StabilityState(stable, reason, span, stableFor, count, latest.Kg);
    }
}
