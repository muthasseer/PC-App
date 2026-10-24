using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Units;

namespace Weighbridge.Core.Tests;

public class StabilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static WeightReading R(double kg, int ms, ReadingFlags flags = ReadingFlags.None) => new()
    {
        Value = (decimal)kg,
        Unit = WeightUnit.Kilogram,
        Flags = flags,
        ReceivedAt = T0.AddMilliseconds(ms),
    };

    [Fact]
    public void Becomes_stable_after_window_within_tolerance()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.Software, WindowMs = 2000, ToleranceKg = 20 });
        StabilityState s = StabilityState.Unknown;
        for (var ms = 0; ms <= 1900; ms += 100) s = d.Add(R(12500 + (ms % 300 == 0 ? 10 : 0), ms));
        Assert.False(s.IsStable);
        Assert.Equal(StabilityReason.Settling, s.Reason);
        s = d.Add(R(12500, 2000));
        Assert.True(s.IsStable);
        Assert.Equal(10m, s.SpanKg);
        Assert.Equal(2000, s.StableForMs);
    }

    [Fact]
    public void Movement_beyond_tolerance_restarts_window()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.Software, WindowMs = 1000, ToleranceKg = 20 });
        for (var ms = 0; ms <= 1500; ms += 100) d.Add(R(12500, ms));
        Assert.True(d.Current.IsStable);
        var s = d.Add(R(12600, 1600)); // truck moved
        Assert.False(s.IsStable);
        Assert.Equal(StabilityReason.OutsideTolerance, s.Reason);
        for (var ms = 1700; ms <= 2500; ms += 100) s = d.Add(R(12600, ms));
        Assert.False(s.IsStable);
        s = d.Add(R(12600, 2600));
        Assert.True(s.IsStable);
    }

    [Fact]
    public void Indicator_motion_flag_overrides_software_in_both_mode()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.Both, WindowMs = 500 });
        for (var ms = 0; ms <= 1000; ms += 100) d.Add(R(5000, ms, ReadingFlags.Stable));
        Assert.True(d.Current.IsStable);
        var s = d.Add(R(5000, 1100, ReadingFlags.Motion));
        Assert.False(s.IsStable);
        Assert.Equal(StabilityReason.IndicatorMotion, s.Reason);
    }

    [Fact]
    public void Indicator_flag_mode_without_flags_is_never_stable()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.IndicatorFlag });
        for (var ms = 0; ms <= 3000; ms += 100) d.Add(R(5000, ms));
        Assert.False(d.Current.IsStable);
        Assert.Equal(StabilityReason.IndicatorNoFlag, d.Current.Reason);
    }

    [Fact]
    public void Data_gap_resets_and_stale_evaluation_is_unstable()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.Software, WindowMs = 500, MaxGapMs = 1000 });
        for (var ms = 0; ms <= 800; ms += 100) d.Add(R(5000, ms));
        Assert.True(d.Current.IsStable);
        Assert.Equal(StabilityReason.DataGap, d.Evaluate(T0.AddMilliseconds(2500)).Reason);
        var s = d.Add(R(5000, 3000));
        Assert.False(s.IsStable); // window restarted after the gap
    }

    [Fact]
    public void Fault_reading_is_unstable()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.Software, WindowMs = 100 });
        for (var ms = 0; ms <= 500; ms += 100) d.Add(R(5000, ms));
        var s = d.Add(new WeightReading { Value = null, Flags = ReadingFlags.Overload, ReceivedAt = T0.AddMilliseconds(600) });
        Assert.False(s.IsStable);
        Assert.Equal(StabilityReason.Fault, s.Reason);
    }

    [Fact]
    public void Too_few_samples_in_window()
    {
        var d = new StabilityDetector(new StabilityOptions { Mode = StabilityMode.Software, WindowMs = 1000, MinSamples = 5, MaxGapMs = 5000 });
        d.Add(R(5000, 0));
        var s = d.Add(R(5000, 1200));
        Assert.False(s.IsStable);
        Assert.Equal(StabilityReason.TooFewSamples, s.Reason);
    }

    [Fact]
    public void Weight_conversions_are_exact()
    {
        Assert.Equal(453.59237m, Weight.From(1000, WeightUnit.Pound).Kilograms);
        Assert.Equal(25400m, Weight.From(25.4m, WeightUnit.Tonne).Kilograms);
        Assert.Equal(12345678L, Weight.FromKilograms(12345.678m).ToGrams());
        Assert.Equal("12,500 kg", WeightDisplayOptions.DefaultKg.Format(Weight.FromKilograms(12500)));
        Assert.Equal("12.50 t", WeightDisplayOptions.DefaultTonnes.Format(Weight.FromKilograms(12500)));
        Assert.Equal("12500", (WeightDisplayOptions.DefaultKg with { ShowUnit = false, UseThousandsSeparator = false }).Format(Weight.FromKilograms(12500)));
    }
}
