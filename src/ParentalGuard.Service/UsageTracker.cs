using Microsoft.Extensions.Logging;
using ParentalGuard.Core.Ipc;
using ParentalGuard.Core.Models;
using ParentalGuard.Service.Data;

namespace ParentalGuard.Service;

/// <summary>
/// The decision engine. Given a <see cref="Heartbeat"/> from a child's session, it:
///   1. figures out how much active time has elapsed since that session's previous heartbeat,
///   2. adds it to the day's total (only when the sample was Active),
///   3. logs the raw sample for reporting,
///   4. decides what the session should do now (nothing / warn / lock),
///   5. returns a <see cref="UsageSnapshot"/> the Agent uses to draw its UI.
///
/// Counting elapsed time between heartbeats — instead of assuming a fixed interval — means a
/// missed or late heartbeat can't be exploited to earn free minutes, and a flood of heartbeats
/// can't inflate the count either (we clamp to a sane maximum).
/// </summary>
public sealed class UsageTracker
{
    private readonly UsageStore _store;
    private readonly ConfigStore _configStore;
    private readonly ILogger<UsageTracker> _log;

    // Per-user timestamp of the last Active heartbeat we counted from.
    private readonly Dictionary<string, DateTimeOffset> _lastActiveSample =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public UsageTracker(UsageStore store, ConfigStore configStore, ILogger<UsageTracker> log)
    {
        _store = store;
        _configStore = configStore;
        _log = log;
    }

    public UsageSnapshot Process(Heartbeat hb)
    {
        var config = _configStore.Load();
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var user = hb.UserName;

        // Always record the raw sample for the parent's activity report.
        _store.LogSample(user, now, hb.State, hb.IdleSeconds);

        int elapsedSecs = 0;
        lock (_gate)
        {
            if (hb.State == ActivityState.Active)
            {
                if (_lastActiveSample.TryGetValue(user, out var prev))
                {
                    var delta = (now - prev).TotalSeconds;
                    // Clamp: never credit less than 0, never more than 2x the heartbeat interval.
                    // That caps the damage from a stalled Agent or a tampered clock jump.
                    var maxCredit = config.HeartbeatSeconds * 2;
                    elapsedSecs = (int)Math.Clamp(delta, 0, maxCredit);
                }
                _lastActiveSample[user] = now;
            }
            else
            {
                // Idle or Locked: stop the clock and drop the anchor so the next Active
                // sample starts a fresh interval rather than back-counting idle time.
                _lastActiveSample.Remove(user);
            }
        }

        int usedMinutes = elapsedSecs > 0
            ? _store.AddActiveSeconds(user, today, elapsedSecs)
            : _store.GetUsedMinutes(user, today);

        // Resolve this child's policy (fall back to a permissive default if unlisted).
        var policy = config.Children.TryGetValue(user, out var p) ? p : null;
        int limit = policy?.DailyLimitMinutes ?? int.MaxValue;
        bool enforcement = policy?.EnforcementEnabled ?? false;

        var localTime = TimeOnly.FromDateTime(now.LocalDateTime);
        bool withinBedtime = policy?.Bedtime.Contains(localTime) ?? false;

        var action = DecideAction(usedMinutes, limit, withinBedtime, policy, enforcement);

        if (action == EnforcementAction.Lock)
            _log.LogInformation("Lock decision for {User}: used {Used}/{Limit} min, bedtime={Bed}",
                user, usedMinutes, limit, withinBedtime);

        return new UsageSnapshot
        {
            Day = today,
            UserName = user,
            UsedMinutes = usedMinutes,
            DailyLimitMinutes = limit == int.MaxValue ? 0 : limit,
            WithinBedtime = withinBedtime,
            Action = action
        };
    }

    private static EnforcementAction DecideAction(
        int usedMinutes, int limit, bool withinBedtime, ChildPolicy? policy, bool enforcement)
    {
        if (!enforcement || policy is null)
            return EnforcementAction.None;

        // Hard stops first.
        if (withinBedtime) return EnforcementAction.Lock;
        if (usedMinutes >= limit) return EnforcementAction.Lock;

        // Soft warning as the limit approaches.
        int remaining = limit - usedMinutes;
        var warnAt = policy.WarnAtMinutes;
        if (warnAt is { Count: > 0 } && remaining <= Max(warnAt))
            return EnforcementAction.Warn;

        return EnforcementAction.None;
    }

    private static int Max(List<int> values)
    {
        int m = values[0];
        for (int i = 1; i < values.Count; i++)
            if (values[i] > m) m = values[i];
        return m;
    }
}
