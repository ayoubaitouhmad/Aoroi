using ParentalGuard.Core.Ipc;
using ParentalGuard.Core.Models;
using ParentalGuard.Agent;

// ParentalGuard Agent — runs inside the child's interactive session.
// Responsibilities (deliberately minimal):
//   - sample idle/lock state,
//   - heartbeat the Service,
//   - act on the Service's decision: warn (console/notification) or lock the workstation.
//
// It has no authority of its own: if the child kills it, the Service simply stops receiving
// heartbeats and stops crediting time — so killing the Agent cannot buy extra screen time.
// (A later step makes Windows relaunch it automatically and show a proper tray UI.)

using var monitor = new SessionMonitor();
using var client = new ServiceClient();

// How often we report in. The Service is the source of truth for the interval meaning,
// but we pace ourselves locally. 15s matches the Service default.
const int tickSeconds = 15;
monitor.IdleThresholdSeconds = 120;

string userName = Environment.UserName;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Log($"Agent started for user '{userName}'. Reporting every {tickSeconds}s.");

// Remember which warning thresholds we've already shown today so we don't nag every tick.
int? lastWarnedRemaining = null;
var warnedDay = DateOnly.FromDateTime(DateTime.Now);

while (!cts.IsCancellationRequested)
{
    var (state, idle) = monitor.Sample();

    var hb = new Heartbeat
    {
        UserName = userName,
        State = state,
        IdleSeconds = idle
    };

    var snapshot = await client.ExchangeAsync(hb, cts.Token);

    if (snapshot is not null)
        HandleSnapshot(snapshot);

    try { await Task.Delay(TimeSpan.FromSeconds(tickSeconds), cts.Token); }
    catch (OperationCanceledException) { break; }
}

Log("Agent stopping.");


void HandleSnapshot(UsageSnapshot s)
{
    // Reset the per-day warning memory at midnight.
    if (s.Day != warnedDay)
    {
        warnedDay = s.Day;
        lastWarnedRemaining = null;
    }

    switch (s.Action)
    {
        case EnforcementAction.Warn:
            if (lastWarnedRemaining != s.RemainingMinutes)
            {
                lastWarnedRemaining = s.RemainingMinutes;
                Notify($"Time's almost up — {s.RemainingMinutes} minute(s) left today.");
            }
            break;

        case EnforcementAction.Lock:
            var reason = s.WithinBedtime ? "bedtime" : "daily limit reached";
            Log($"Enforcement lock ({reason}). Locking workstation.");
            Notify($"Locking now: {reason}.");
            NativeMethods.LockWorkStation();
            break;

        case EnforcementAction.None:
        default:
            break;
    }
}

// Placeholder notification sink. The next milestone swaps this for a real tray balloon /
// toast and an on-screen countdown; for now it goes to the console so we can test end-to-end.
void Notify(string message) => Log($"[NOTIFY] {message}");

void Log(string message) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");
