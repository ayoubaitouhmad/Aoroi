namespace ParentalGuard.Core.Models;

/// <summary>
/// The rules the parent sets. Loaded by the Service from a JSON file in ProgramData
/// (writable only by administrators). The web panel will edit this same file later.
/// Everything here is deliberately data, not code, so the open-source build ships with
/// sane defaults and each parent tunes their own copy without recompiling.
/// </summary>
public sealed class PolicyConfig
{
    /// <summary>Per-child rules, keyed by Windows account name (case-insensitive).</summary>
    public Dictionary<string, ChildPolicy> Children { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often the Agent reports in, in seconds. Lower = finer counting, more chatter.</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>Idle beyond this many seconds stops the usage clock. Covers bathroom breaks, reading, etc.</summary>
    public int IdleThresholdSeconds { get; set; } = 120;

    /// <summary>Default config used on first run, before the parent customizes anything.</summary>
    public static PolicyConfig Default() => new()
    {
        Children =
        {
            ["child"] = new ChildPolicy
            {
                DailyLimitMinutes = 120,
                Bedtime = new BedtimeWindow { Start = new TimeOnly(21, 0), End = new TimeOnly(7, 0) },
                WarnAtMinutes = new List<int> { 10, 5, 1 }
            }
        }
    };
}

public sealed class ChildPolicy
{
    /// <summary>Minutes of active use allowed per day.</summary>
    public int DailyLimitMinutes { get; set; } = 120;

    /// <summary>Hours during which the PC is off-limits regardless of remaining time.</summary>
    public BedtimeWindow Bedtime { get; set; } = new();

    /// <summary>"Minutes remaining" thresholds at which to warn the child. Sorted high-to-low at use.</summary>
    public List<int> WarnAtMinutes { get; set; } = new() { 10, 5, 1 };

    /// <summary>Set false to pause enforcement for this child (still counts, just doesn't lock).</summary>
    public bool EnforcementEnabled { get; set; } = true;
}

/// <summary>
/// A bedtime window that may cross midnight (e.g. 21:00 → 07:00).
/// </summary>
public sealed class BedtimeWindow
{
    public TimeOnly Start { get; set; } = new(21, 0);
    public TimeOnly End { get; set; } = new(7, 0);

    /// <summary>True if <paramref name="now"/> falls inside the window, handling the midnight wrap.</summary>
    public bool Contains(TimeOnly now)
    {
        // Same-day window, e.g. 13:00 -> 15:00
        if (Start <= End)
            return now >= Start && now < End;

        // Wraps midnight, e.g. 21:00 -> 07:00
        return now >= Start || now < End;
    }
}
