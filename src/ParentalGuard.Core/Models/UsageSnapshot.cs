namespace ParentalGuard.Core.Models;

/// <summary>
/// A read-only view of today's usage for one child account, as the Service sees it.
/// This is what the Agent receives back after each heartbeat, so the tray UI can show
/// "time remaining" without doing any counting of its own.
/// </summary>
public sealed record UsageSnapshot
{
    /// <summary>The local calendar day these figures belong to (midnight-to-midnight).</summary>
    public required DateOnly Day { get; init; }

    /// <summary>Windows account the figures are for (e.g. "child").</summary>
    public required string UserName { get; init; }

    /// <summary>Active minutes already spent today.</summary>
    public int UsedMinutes { get; init; }

    /// <summary>The configured daily allowance, in minutes.</summary>
    public int DailyLimitMinutes { get; init; }

    /// <summary>Minutes still available today. Never negative.</summary>
    public int RemainingMinutes => Math.Max(0, DailyLimitMinutes - UsedMinutes);

    /// <summary>True once the daily limit has been reached.</summary>
    public bool LimitReached => UsedMinutes >= DailyLimitMinutes;

    /// <summary>True if the current local time falls inside the configured bedtime window.</summary>
    public bool WithinBedtime { get; init; }

    /// <summary>
    /// The enforcement action the Service wants applied right now. The Agent shows warnings;
    /// the Service itself performs the lock/logoff so it cannot be dodged by killing the tray app.
    /// </summary>
    public EnforcementAction Action { get; init; } = EnforcementAction.None;
}

/// <summary>What should happen to the session right now, decided by the Service.</summary>
public enum EnforcementAction
{
    /// <summary>Nothing — keep going.</summary>
    None = 0,

    /// <summary>Show a countdown warning to the child (time almost up / bedtime approaching).</summary>
    Warn = 1,

    /// <summary>Lock the workstation. The child can log back in only if time/bedtime allows.</summary>
    Lock = 2
}
