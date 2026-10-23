namespace Weighbridge.Core.Automation;

public sealed record ExitInterlockContext(
    bool CaptureSaved,
    bool VehicleOnDeck,
    bool SafetyOk,
    FaultKind ActiveFault,
    bool ManualOverride = false,
    bool OverrideAuthorized = false);

public sealed record EntryInterlockContext(
    bool DeckEmpty,
    bool IndicatorHealthy,
    bool SafetyOk,
    FaultKind ActiveFault,
    bool ManualOverride = false,
    bool OverrideAuthorized = false);

public readonly record struct InterlockResult(bool Allowed, string Reason)
{
    public static InterlockResult Ok(string reason = "Interlocks satisfied.") => new(true, reason);
    public static InterlockResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// The only place that decides whether a barrier may open. A valid-looking weight string is never enough: the
/// transaction must be saved, the vehicle must be on the deck, safety inputs must be healthy and no fault may be
/// active. A manual override needs an authorised user and still respects the safety inputs.
/// </summary>
public static class BarrierInterlock
{
    public static InterlockResult CanOpenExit(ExitInterlockContext c)
    {
        if (!c.SafetyOk) return InterlockResult.Deny("Safety interlock is not satisfied (barrier area not clear or emergency stop).");
        if (c.ManualOverride)
        {
            return c.OverrideAuthorized
                ? InterlockResult.Ok("Manual override by an authorised user.")
                : InterlockResult.Deny("Manual barrier override requires the hardware override permission.");
        }
        if (c.ActiveFault != FaultKind.None) return InterlockResult.Deny($"A fault is active ({c.ActiveFault}).");
        if (!c.CaptureSaved) return InterlockResult.Deny("The weighing has not been captured and saved.");
        if (!c.VehicleOnDeck) return InterlockResult.Deny("No vehicle is detected on the weighbridge.");
        return InterlockResult.Ok();
    }

    public static InterlockResult CanOpenEntry(EntryInterlockContext c)
    {
        if (!c.SafetyOk) return InterlockResult.Deny("Safety interlock is not satisfied.");
        if (c.ManualOverride)
        {
            return c.OverrideAuthorized
                ? InterlockResult.Ok("Manual override by an authorised user.")
                : InterlockResult.Deny("Manual barrier override requires the hardware override permission.");
        }
        if (c.ActiveFault != FaultKind.None) return InterlockResult.Deny($"A fault is active ({c.ActiveFault}).");
        if (!c.IndicatorHealthy) return InterlockResult.Deny("The indicator is not delivering valid readings.");
        if (!c.DeckEmpty) return InterlockResult.Deny("The weighbridge is not empty.");
        return InterlockResult.Ok();
    }
}
