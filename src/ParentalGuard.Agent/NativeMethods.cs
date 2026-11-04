using System.Runtime.InteropServices;

namespace ParentalGuard.Agent;

/// <summary>
/// The small set of Win32 calls the Agent needs: measuring input-idle time and locking
/// the workstation. Kept in one place so the P/Invoke surface is easy to audit — important
/// for an open-source tool where people will (rightly) want to see exactly what it touches.
/// </summary>
internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>Fills in the tick count of the last keyboard/mouse input, system-wide for this session.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Locks the current interactive session (same as pressing Win+L).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

    /// <summary>Milliseconds since the system started (wraps ~49 days; fine for idle math).</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial ulong GetTickCount64();

    /// <summary>
    /// Seconds since the last user input in this session. 0 means "active right now".
    /// </summary>
    internal static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return 0;

        ulong now = GetTickCount64();
        ulong idleMs = now - info.dwTime;
        return (int)(idleMs / 1000);
    }
}
