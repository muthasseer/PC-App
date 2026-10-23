using Weighbridge.Core.Units;

namespace Weighbridge.Core.Weighing;

public sealed record NetResult(
    Weight Gross,
    Weight Tare,
    Weight Net,
    bool FirstIsGross,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record NetRules
{
    public NetAssignmentRule Rule { get; init; } = NetAssignmentRule.HeavierIsGross;

    /// <summary>Net weights below this are flagged (e.g. empty vehicle weighed twice).</summary>
    public decimal MinimumNetKg { get; init; } = 20m;

    /// <summary>Treat a net below <see cref="MinimumNetKg"/> as an error instead of a warning.</summary>
    public bool RejectBelowMinimumNet { get; init; }

    /// <summary>Warn when the second weighing comes later than this after the first (hours).</summary>
    public double MaxPendingHours { get; init; } = 48;
}

public static class NetCalculator
{
    public static NetResult Calculate(
        Weight first,
        Weight second,
        TransactionDirection direction,
        NetRules rules,
        DateTimeOffset? firstAt = null,
        DateTimeOffset? secondAt = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (first.IsNegative || second.IsNegative) errors.Add("A weighing is negative; gross/tare cannot be calculated.");

        bool firstIsGross;
        switch (rules.Rule)
        {
            case NetAssignmentRule.ByDirection when direction == TransactionDirection.Inbound:
                firstIsGross = true;
                if (first < second) errors.Add("Inbound: the first weighing should be the loaded (gross) weight but it is lighter than the second.");
                break;
            case NetAssignmentRule.ByDirection when direction == TransactionDirection.Outbound:
                firstIsGross = false;
                if (first > second) errors.Add("Outbound: the first weighing should be the empty (tare) weight but it is heavier than the second.");
                break;
            default:
                firstIsGross = first >= second;
                break;
        }

        var gross = firstIsGross ? first : second;
        var tare = firstIsGross ? second : first;
        var net = gross - tare;
        if (net.IsNegative)
        {
            // Only reachable when the direction rule was violated; keep the sign visible but never finalise.
            errors.Add("Net weight is negative.");
        }

        if (net.Abs().Kilograms < rules.MinimumNetKg)
        {
            var message = $"Net weight {net.Kilograms:0.###} kg is below the minimum of {rules.MinimumNetKg:0.###} kg.";
            if (rules.RejectBelowMinimumNet) errors.Add(message);
            else warnings.Add(message);
        }

        if (firstAt is { } fa && secondAt is { } sa)
        {
            if (sa < fa) errors.Add("The second weighing is dated before the first weighing.");
            else if ((sa - fa).TotalHours > rules.MaxPendingHours) warnings.Add($"The second weighing is {(sa - fa).TotalHours:0} hours after the first.");
        }

        return new NetResult(gross, tare, net, firstIsGross, errors, warnings);
    }

    /// <summary>Single weighing against an approved stored tare (or an authorised manual tare).</summary>
    public static NetResult WithKnownTare(Weight measured, Weight tare, NetRules rules)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (tare.IsNegative) errors.Add("Tare is negative.");
        if (measured < tare) errors.Add("The measured weight is lighter than the tare.");
        var net = measured - tare;
        if (net.Abs().Kilograms < rules.MinimumNetKg)
        {
            var message = $"Net weight {net.Kilograms:0.###} kg is below the minimum of {rules.MinimumNetKg:0.###} kg.";
            if (rules.RejectBelowMinimumNet) errors.Add(message);
            else warnings.Add(message);
        }
        return new NetResult(measured, tare, net, true, errors, warnings);
    }
}
