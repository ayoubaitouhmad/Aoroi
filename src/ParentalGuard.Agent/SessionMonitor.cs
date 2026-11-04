using Microsoft.Win32;
using ParentalGuard.Core.Models;

namespace ParentalGuard.Agent;

/// <summary>
/// Reads the current session's activity: idle seconds from Win32, and whether the session is
/// locked. Lock state is tracked via <see cref="SystemEvents.SessionSwitch"/>, which fires on
/// lock/unlock; we start assuming unlocked (the Agent only runs while someone is logged in).
/// </summary>
public sealed class SessionMonitor : IDisposable
{
    private volatile bool _locked;

    public SessionMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => _locked
        };
    }

    /// <summary>The idle threshold above which we treat the session as not actively used.</summary>
    public int IdleThresholdSeconds { get; set; } = 120;

    /// <summary>Sample the session right now.</summary>
    public (ActivityState State, int IdleSeconds) Sample()
    {
        if (_locked)
            return (ActivityState.Locked, 0);

        int idle = NativeMethods.GetIdleSeconds();
        var state = idle >= IdleThresholdSeconds ? ActivityState.Idle : ActivityState.Active;
        return (state, idle);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
