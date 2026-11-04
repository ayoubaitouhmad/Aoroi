namespace ParentalGuard.Core.Models;

/// <summary>
/// Whether the child's session is actively being used at the moment a sample is taken.
/// The Agent (running inside the child's session) decides this from input-idle time
/// and whether the session is locked; the Service only counts <see cref="Active"/> samples.
/// </summary>
public enum ActivityState
{
    /// <summary>User is present and interacting (input seen within the idle threshold).</summary>
    Active = 0,

    /// <summary>No input for longer than the idle threshold, but the session is unlocked.</summary>
    Idle = 1,

    /// <summary>The session is locked (Win+L, screensaver lock, or our own enforcement lock).</summary>
    Locked = 2
}
