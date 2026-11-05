using System.Drawing;
using ParentalGuard.Agent.Ui;
using ParentalGuard.Core.Ipc;
using ParentalGuard.Core.Models;

namespace ParentalGuard.Agent;

/// <summary>
/// The Agent's "application" — a tray-resident context with no main window.
///
/// It owns:
///   - a <see cref="NotifyIcon"/> (the tray icon + its tooltip and a read-only status menu),
///   - a <see cref="CountdownOverlay"/> (the small "X min left" banner shown near the limit),
///   - a <see cref="TimesUpOverlay"/> (the full-screen "time's up / bedtime" screen),
///   - the heartbeat loop that talks to the Service.
///
/// Threading note (important): the heartbeat loop is started on the UI thread and never uses
/// ConfigureAwait(false). Because WinForms installs a SynchronizationContext, every `await`
/// continuation resumes back on the UI thread — so we can touch the tray and the overlay forms
/// directly from <see cref="HandleSnapshot"/> without Control.Invoke. The network calls inside
/// ExchangeAsync are genuinely async, so the UI never freezes while waiting on the pipe.
/// </summary>
public sealed class AgentApplicationContext : ApplicationContext
{
    // These mirror the Service defaults. The Agent is still "dumb": it only paces itself and
    // reports; all limits/decisions come back from the Service in the snapshot.
    private const int TickSeconds = 15;
    private const int IdleThresholdSeconds = 120;

    private readonly SessionMonitor _monitor = new();
    private readonly ServiceClient _client = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly CountdownOverlay _countdown = new();
    private readonly TimesUpOverlay _timesUp = new();

    private readonly string _userName = Environment.UserName;

    // Avoid showing the same warning balloon every single tick.
    private int? _lastWarnedRemaining;
    private DateOnly _warnedDay = DateOnly.FromDateTime(DateTime.Now);

    public AgentApplicationContext()
    {
        _monitor.IdleThresholdSeconds = IdleThresholdSeconds;

        // A read-only status line in the tray menu. Deliberately NO "Exit" item — we don't
        // give the child a one-click way to shut the Agent down from the menu.
        _statusItem = new ToolStripMenuItem("ParentalGuard — starting…") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);

        _tray = new NotifyIcon
        {
            // Use a built-in system icon so we don't have to ship a binary .ico asset.
            Icon = SystemIcons.Shield,
            Text = "ParentalGuard",
            Visible = true,
            ContextMenuStrip = menu
        };

        // Kick off the loop on the UI thread (see the threading note above).
        _ = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (state, idle) = _monitor.Sample();

            var hb = new Heartbeat
            {
                UserName = _userName,
                State = state,
                IdleSeconds = idle
            };

            UsageSnapshot? snapshot;
            try
            {
                snapshot = await _client.ExchangeAsync(hb, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (snapshot is not null)
                HandleSnapshot(snapshot);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs on the UI thread. Updates the tray and shows/hides the overlays.</summary>
    private void HandleSnapshot(UsageSnapshot s)
    {
        // --- Tray tooltip + status menu line ---
        string status = s.DailyLimitMinutes > 0
            ? $"{s.RemainingMinutes} min left today"
            : "No limit set";
        _statusItem.Text = $"ParentalGuard — {status}";
        // NotifyIcon.Text is capped at 63 characters by Windows; keep it short.
        _tray.Text = Truncate($"ParentalGuard · {status}", 63);

        // Reset the per-day warning memory at midnight.
        if (s.Day != _warnedDay)
        {
            _warnedDay = s.Day;
            _lastWarnedRemaining = null;
        }

        switch (s.Action)
        {
            case EnforcementAction.Warn:
                _timesUp.HideOverlay();
                _countdown.ShowRemaining(s.RemainingMinutes, s.WithinBedtime);

                // Only pop a balloon when the remaining-minutes number actually changes,
                // so we nudge at each threshold instead of nagging every 15 seconds.
                if (_lastWarnedRemaining != s.RemainingMinutes)
                {
                    _lastWarnedRemaining = s.RemainingMinutes;
                    _tray.ShowBalloonTip(
                        5000,
                        "ParentalGuard",
                        $"Time's almost up — {s.RemainingMinutes} minute(s) left today.",
                        ToolTipIcon.Warning);
                }
                break;

            case EnforcementAction.Lock:
                _countdown.HideOverlay();
                string reason = s.WithinBedtime
                    ? "It's bedtime."
                    : "Your screen time is up for today.";
                _timesUp.ShowOverlay(reason);
                // Hard enforcement: lock the workstation. If the child logs back in while still
                // over the limit, the next heartbeat returns Lock again and we re-lock.
                NativeMethods.LockWorkStation();
                break;

            case EnforcementAction.None:
            default:
                _countdown.HideOverlay();
                _timesUp.HideOverlay();
                _lastWarnedRemaining = null;
                break;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();

            _tray.Visible = false;
            _tray.Dispose();
            _countdown.Dispose();
            _timesUp.Dispose();
            _monitor.Dispose();
            _client.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
