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

    /// <summary>DNS content-filter settings (machine-wide, not per-child).</summary>
    public DnsConfig Dns { get; set; } = new();

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
/// Settings for the local DNS content filter. These are machine-wide: the filter runs once
/// inside the Service and serves every lookup from the PC. For Step 1 it only uses the listen
/// address/port and upstream servers (it's a pure forwarder); the blocklist/category fields
/// arrive in later steps.
/// </summary>
public sealed class DnsConfig
{
    /// <summary>Master switch for the DNS filter. When false, the Service doesn't open the listener.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Address to listen on. Loopback only by default — nothing off this machine can use it.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>DNS port. The standard is 53; the adapter will be pointed here in a later step.</summary>
    public int Port { get; set; } = 53;

    /// <summary>
    /// Upstream resolvers we forward allowed queries to, tried in order. Cloudflare by default;
    /// later you can swap in a family resolver (e.g. CleanBrowsing) to get porn filtering for free
    /// underneath our own category blocking.
    /// </summary>
    public List<string> UpstreamServers { get; set; } = new() { "1.1.1.1", "1.0.0.1" };

    /// <summary>How long to wait for an upstream reply before trying the next one, in milliseconds.</summary>
    public int UpstreamTimeoutMs { get; set; } = 3000;
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
