using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Core.Tests;

public class CaptureValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly CaptureLimits Limits = new() { MinimumKg = 200, CapacityKg = 60000, FreshnessMs = 1500 };

    private static IndicatorSnapshot Snap(decimal? kg, bool stable = true, int ageMs = 100, ReadingFlags flags = ReadingFlags.None,
        ConnectionState connection = ConnectionState.Connected, DateTimeOffset? rejectAt = null) => new()
    {
        Connection = connection,
        Latest = kg is null && flags == ReadingFlags.None ? null : new WeightReading { Value = kg, Flags = flags, ReceivedAt = Now.AddMilliseconds(-ageMs), Text = $"{kg}" },
        Stability = stable ? new StabilityState(true, StabilityReason.Stable, 0, 2000, 20, kg) : new StabilityState(false, StabilityReason.OutsideTolerance, 80, 0, 3, kg),
        LastRejectAt = rejectAt,
        LastRejectReason = rejectAt is null ? null : "noise",
        ProfileName = "test",
        Timestamp = Now,
    };

    private static CaptureDecision Eval(IndicatorSnapshot s, UnstableCapturePolicy policy = UnstableCapturePolicy.WaitForStable) =>
        CaptureValidator.Evaluate(s, Now, Limits, new CapturePolicy { Unstable = policy });

    [Fact]
    public void Stable_fresh_valid_reading_is_allowed()
    {
        var d = Eval(Snap(12500));
        Assert.True(d.IsAllowed);
        Assert.Empty(d.Issues);
    }

    [Theory]
    [InlineData(CaptureIssueCode.NotConnected)]
    [InlineData(CaptureIssueCode.NoReading)]
    [InlineData(CaptureIssueCode.Stale)]
    [InlineData(CaptureIssueCode.LatestFrameInvalid)]
    [InlineData(CaptureIssueCode.Overload)]
    [InlineData(CaptureIssueCode.IndicatorError)]
    [InlineData(CaptureIssueCode.Negative)]
    [InlineData(CaptureIssueCode.BelowMinimum)]
    [InlineData(CaptureIssueCode.AboveCapacity)]
    public void Invalid_conditions_are_rejected(CaptureIssueCode code)
    {
        var s = code switch
        {
            CaptureIssueCode.NotConnected => Snap(12500, connection: ConnectionState.Silent),
            CaptureIssueCode.NoReading => Snap(null),
            CaptureIssueCode.Stale => Snap(12500, ageMs: 5000),
            CaptureIssueCode.LatestFrameInvalid => Snap(12500, rejectAt: Now.AddMilliseconds(-10)),
            CaptureIssueCode.Overload => Snap(null, flags: ReadingFlags.Overload),
            CaptureIssueCode.IndicatorError => Snap(5, flags: ReadingFlags.Error),
            CaptureIssueCode.Negative => Snap(-500),
            CaptureIssueCode.BelowMinimum => Snap(150),
            _ => Snap(70000),
        };
        var d = Eval(s);
        Assert.Equal(CaptureVerdict.Rejected, d.Verdict);
        Assert.Equal(code, d.Issues.Single().Code);
    }

    [Theory]
    [InlineData(UnstableCapturePolicy.Reject, CaptureVerdict.Rejected)]
    [InlineData(UnstableCapturePolicy.WaitForStable, CaptureVerdict.WaitForStability)]
    [InlineData(UnstableCapturePolicy.SupervisorOverride, CaptureVerdict.OverrideRequired)]
    public void Unstable_weight_follows_policy(UnstableCapturePolicy policy, CaptureVerdict expected)
    {
        var d = Eval(Snap(12500, stable: false), policy);
        Assert.Equal(expected, d.Verdict);
        Assert.Equal(CaptureIssueCode.Unstable, d.Issues.Single().Code);
    }

    [Fact]
    public void Captured_weight_carries_evidence()
    {
        var s = Snap(12500) with { PortDescription = "COM3 9600 8N1", ProfileId = Guid.NewGuid(), ProfileVersion = 4 };
        var d = Eval(s);
        var c = CapturedWeight.FromDecision(d, s, Now, OperatingMode.SemiAutomatic, []);
        Assert.Equal(12500m, c.Weight.Kilograms);
        Assert.Equal(100, c.ReadingAgeMs);
        Assert.Equal("COM3 9600 8N1", c.PortDescription);
        Assert.Equal(4, c.ProfileVersion);
        Assert.Equal(MeasurementSource.Indicator, c.Source);
        Assert.True(c.IsStable);
    }

    [Fact]
    public void Display_status_colours()
    {
        Assert.Equal(DisplayColor.Green, DisplayStatusEvaluator.Evaluate(Snap(12500), Now, Limits, OperatingMode.SemiAutomatic, false).Color);
        Assert.Equal(DisplayColor.Yellow, DisplayStatusEvaluator.Evaluate(Snap(12500, stable: false), Now, Limits, OperatingMode.SemiAutomatic, false).Color);
        Assert.Equal(DisplayColor.Red, DisplayStatusEvaluator.Evaluate(Snap(null, flags: ReadingFlags.Overload), Now, Limits, OperatingMode.SemiAutomatic, false).Color);
        Assert.Equal(DisplayColor.Red, DisplayStatusEvaluator.Evaluate(Snap(12500, ageMs: 9000), Now, Limits, OperatingMode.SemiAutomatic, false).Color);
        Assert.Equal(DisplayColor.Blue, DisplayStatusEvaluator.Evaluate(Snap(12500), Now, Limits, OperatingMode.SemiAutomatic, true).Color);
        Assert.Equal(DisplayColor.Grey, DisplayStatusEvaluator.Evaluate(IndicatorSnapshot.Disconnected(Now), Now, Limits, OperatingMode.SemiAutomatic, false).Color);
        Assert.Equal(DisplayCondition.ManualMode, DisplayStatusEvaluator.Evaluate(IndicatorSnapshot.Disconnected(Now), Now, Limits, OperatingMode.Manual, false).Condition);
        Assert.Equal(DisplayCondition.Zero, DisplayStatusEvaluator.Evaluate(Snap(0), Now, Limits, OperatingMode.SemiAutomatic, false).Condition);
    }
}

public class NetCalculatorTests
{
    private static readonly NetRules Rules = new();

    [Theory]
    [InlineData(32500, 12300, TransactionDirection.Inbound, 32500, 12300, 20200, true)]   // enters loaded
    [InlineData(12300, 32500, TransactionDirection.Outbound, 32500, 12300, 20200, false)] // enters empty
    [InlineData(12300, 32500, TransactionDirection.Inbound, 32500, 12300, 20200, false)]  // heavier-is-gross handles both
    public void Heavier_is_gross(decimal first, decimal second, TransactionDirection dir, decimal gross, decimal tare, decimal net, bool firstIsGross)
    {
        var r = NetCalculator.Calculate(Weight.FromKilograms(first), Weight.FromKilograms(second), dir, Rules);
        Assert.True(r.IsValid);
        Assert.Equal(gross, r.Gross.Kilograms);
        Assert.Equal(tare, r.Tare.Kilograms);
        Assert.Equal(net, r.Net.Kilograms);
        Assert.Equal(firstIsGross, r.FirstIsGross);
    }

    [Fact]
    public void By_direction_detects_contradiction()
    {
        var rules = Rules with { Rule = NetAssignmentRule.ByDirection };
        Assert.True(NetCalculator.Calculate(Weight.FromKilograms(32500), Weight.FromKilograms(12300), TransactionDirection.Inbound, rules).IsValid);
        var bad = NetCalculator.Calculate(Weight.FromKilograms(12300), Weight.FromKilograms(32500), TransactionDirection.Inbound, rules);
        Assert.False(bad.IsValid);
        Assert.Contains(bad.Errors, e => e.Contains("Inbound"));
        Assert.True(NetCalculator.Calculate(Weight.FromKilograms(12300), Weight.FromKilograms(32500), TransactionDirection.Outbound, rules).IsValid);
    }

    [Fact]
    public void Minimum_net_and_timing_checks()
    {
        var t = DateTimeOffset.UtcNow;
        var warn = NetCalculator.Calculate(Weight.FromKilograms(12300), Weight.FromKilograms(12310), TransactionDirection.Inbound, Rules, t, t.AddHours(72));
        Assert.True(warn.IsValid);
        Assert.Equal(2, warn.Warnings.Count);
        var strict = NetCalculator.Calculate(Weight.FromKilograms(12300), Weight.FromKilograms(12310), TransactionDirection.Inbound, Rules with { RejectBelowMinimumNet = true });
        Assert.False(strict.IsValid);
        var backwards = NetCalculator.Calculate(Weight.FromKilograms(32300), Weight.FromKilograms(12310), TransactionDirection.Inbound, Rules, t, t.AddMinutes(-5));
        Assert.False(backwards.IsValid);
    }

    [Fact]
    public void Stored_tare()
    {
        var r = NetCalculator.WithKnownTare(Weight.FromKilograms(32500), Weight.FromKilograms(12300), Rules);
        Assert.True(r.IsValid);
        Assert.Equal(20200m, r.Net.Kilograms);
        Assert.False(NetCalculator.WithKnownTare(Weight.FromKilograms(10000), Weight.FromKilograms(12300), Rules).IsValid);
    }
}

public class TicketNumberingTests
{
    [Fact]
    public void Formats_and_scopes()
    {
        var o = new TicketNumberOptions();
        var d = new DateTime(2026, 3, 9);
        Assert.Equal("CMB-2026-000042", TicketNumbering.Format(o, "cmb", "WB1", d, 42));
        Assert.Equal("ticket|cmb|*|2026", TicketNumbering.ScopeKey(o, "cmb", "WB1", d));

        var daily = new TicketNumberOptions { Pattern = "{WB}/{YY}{MM}{DD}/{SEQ:4}", Reset = SequenceReset.Daily };
        Assert.Equal("WB2/260309/0007", TicketNumbering.Format(daily, "S", "wb2", d, 7));
        Assert.Equal("ticket|S|wb2|2026-03-09", TicketNumbering.ScopeKey(daily, "S", "wb2", d));
        Assert.Empty(TicketNumbering.Validate(daily));
    }

    [Fact]
    public void Validation_catches_repeating_patterns()
    {
        Assert.NotEmpty(TicketNumbering.Validate(new TicketNumberOptions { Pattern = "{SITE}-{YYYY}" }));
        Assert.NotEmpty(TicketNumbering.Validate(new TicketNumberOptions { Pattern = "{SITE}-{SEQ}", Reset = SequenceReset.Yearly }));
        Assert.Empty(TicketNumbering.Validate(new TicketNumberOptions { Pattern = "{SITE}-{SEQ:8}", Reset = SequenceReset.Never }));
    }

    [Theory]
    [InlineData("WP CAB-1234", "WPCAB1234", "WP CAB-1234")]
    [InlineData("  wp   cab 1234 ", "WPCAB1234", "WP CAB 1234")]
    [InlineData("\u0DC1\u0DCA\u200D\u0DBB\u0DD3 12-3456", "\u0DC1\u0DCA\u0DBB\u0DD3123456", "\u0DC1\u0DCA\u200D\u0DBB\u0DD3 12-3456")]
    public void Vehicle_registration_normalisation(string input, string normalized, string display)
    {
        Assert.Equal(normalized, VehicleRegistration.Normalize(input));
        Assert.Equal(display, VehicleRegistration.Display(input));
    }
}
