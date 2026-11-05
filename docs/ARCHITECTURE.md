# ParentalGuard — Code Walkthrough

A guided tour of how the project is built and what every class does. Written for someone
comfortable with **WinForms** and **ASP.NET** but new to **Windows Services**. Wherever a
concept is new, it's mapped back to something you already know from ASP.NET.

Read this top-to-bottom once, then keep section 5 open as a reference while you poke around
the code in Rider.

---

## 1. The big picture — why there are *two* programs

The single most important idea in the whole project:

> A Windows **Service** runs in an isolated area called **"Session 0"**. Code there has **no
> desktop**. It cannot read the user's keyboard/mouse idle time, and it cannot show a window,
> a message box, or a tray icon. The logged-in user lives in **Session 1** (or 2, 3… with fast
> user switching).

This is a deliberate Windows security boundary introduced back in Windows Vista ("Session 0
Isolation"). It stops a background service from being tricked into clicking a dialog that a
malicious program popped up.

That boundary forces our design. We need two things that *can't live in the same process*:

1. Something **protected and always-on** that the child can't kill — that has to be a Service
   (Session 0, running as SYSTEM).
2. Something that can **see the desktop** — measure idle time, show warnings, lock the screen —
   that has to run inside the child's own session.

So the project is split in two cooperating programs:

```
   CHILD'S SESSION (Session 1)                 SESSION 0  (runs as LocalSystem)
┌──────────────────────────────┐            ┌────────────────────────────────────┐
│  ParentalGuard.Agent          │  heartbeat │  ParentalGuard.Service              │
│  (a normal .exe in the        │───────────►│  (a Windows Service)                │
│   child's session)            │            │                                      │
│                               │◄───────────│   decides: nothing / warn / lock     │
│  • measure idle / lock state  │  snapshot  │   counts + stores the time           │
│  • show warnings              │            │   holds the rules (policy.json)      │
│  • LOCK the screen on command │            │                                      │
└──────────────────────────────┘            └────────────────────────────────────┘
          can see the desktop                       protected, cannot be killed
          but has NO authority                      by a standard user
```

The **Agent is deliberately powerless**. It never decides anything. It reports what it sees and
obeys. So if the child discovers the Agent and kills it, nothing bad happens for them: the
Service simply stops receiving heartbeats and **stops crediting time**. There is no way to kill
the Agent to *gain* screen time. All the authority lives in the Service, which they can't touch.

They talk over a **named pipe** — a local, in-memory channel that never touches the network
(more on that in section 3).

---

## 2. The three projects and how they depend on each other

```
ParentalGuard.Core      ← shared vocabulary. No logic. No dependencies.
      ▲        ▲
      │        │
ParentalGuard.Service   ParentalGuard.Agent
(the brain)             (the reporter)
```

- **ParentalGuard.Core** — just the *data shapes* both sides agree on: the messages they send
  each other and the config format. It contains no behaviour. Think of it as the "contract"
  assembly. Both other projects reference it, so when the Service serializes a `UsageSnapshot`
  and the Agent deserializes it, they're guaranteed to use the *exact same class*.
- **ParentalGuard.Service** — references Core. The decision-making, storage, and the pipe server.
- **ParentalGuard.Agent** — references Core. The session sampling, the pipe client, the lock.

Service and Agent **do not** reference each other. Their only connection is the pipe, using the
shared types from Core. This is the same discipline as splitting an ASP.NET solution into
`Api`, `Application`, and `Contracts` projects — the contract project in the middle keeps the
two ends honest.

---

## 3. New concepts (mapped to ASP.NET you already know)

### 3a. The Generic Host — *you've already seen this*

Open `ParentalGuard.Service/Program.cs`. The first real line is:

```csharp
var builder = Host.CreateApplicationBuilder(args);
```

This is the **exact same host builder** you get in a modern ASP.NET app (`WebApplication.CreateBuilder`
is just a web-flavoured wrapper around it). You get:

- a **dependency injection container** (`builder.Services.AddSingleton<…>()`) — identical to ASP.NET,
- **configuration**,
- **logging** (`builder.Logging…`),
- a **lifetime** that starts things on boot and stops them cleanly on shutdown.

So a Windows Service in modern .NET is *not* some exotic API. It's the same host you already know,
with one extra line:

```csharp
builder.Services.AddWindowsService(options => { options.ServiceName = "ParentalGuard"; });
```

That line tells .NET "when I'm launched by the Windows Service Control Manager, behave like a
service (report start/stop status, log to the Event Log, etc.)". When you run it from Rider with
`dotnet run`, that same code just runs as a console app — which is why our dev loop works without
installing anything. **One codebase, two ways to run.**

### 3b. `BackgroundService` — the "forever loop" base class

In ASP.NET you might have written an `IHostedService` or a `BackgroundService` for a job that runs
for the lifetime of the app (a queue consumer, a timer). Same thing here. Look at
`Workers/IpcWorker.cs`:

```csharp
public sealed class IpcWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _server.RunAsync(stoppingToken);
    }
}
```

`BackgroundService` is a helper base class with one method to override: `ExecuteAsync`. The host
calls it on startup and hands you a `CancellationToken` that it **signals when the service is
stopping**. That's how a clean "stop" flows all the way down: Windows tells the host to stop →
the host cancels the token → our pipe loop sees the cancellation and exits. You register it the
same way you'd register a hosted service in ASP.NET:

```csharp
builder.Services.AddHostedService<IpcWorker>();
```

### 3c. The Windows Service lifecycle & "LocalSystem"

A Windows Service is a program registered with the OS that can start **at boot, before anyone
logs in**, and keep running regardless of who is (or isn't) logged in. It runs under a *service
account*. We use **LocalSystem**, the highest-privilege built-in account. Two consequences:

- It starts automatically and a **standard user cannot stop or uninstall it** (that's the point).
- It can write to `C:\ProgramData`, which is admin/SYSTEM-writable but read-only to standard users.

`tools/install-service.ps1` is what registers it (via `New-Service` + `sc.exe config … obj= LocalSystem`).
Until you run that, you're just running it as a console app in your own session for testing.

### 3d. Named pipes — "HTTP, but local-only"

The Agent and Service need to talk. In ASP.NET your instinct would be an HTTP endpoint. We
deliberately **don't** use the network here, because:

- the traffic should never be reachable from another machine,
- we want to restrict *who* can connect using Windows security, not a password.

A **named pipe** is a local inter-process channel with a name (`\\.\pipe\ParentalGuard.Ipc.v1`).
It behaves like a two-way stream (you read/write bytes, same as a `NetworkStream`), but it lives
entirely inside the OS and can carry a **security descriptor (ACL)** saying exactly which accounts
may connect. Mentally: *it's a socket that never leaves the box and has Windows ACLs built in.*

We send **one JSON object per line** over it. The Agent writes a `Heartbeat` line; the Service
writes back a `UsageSnapshot` line. That newline-delimited-JSON choice is just a simple, readable
framing — the same idea as a tiny line-based protocol.

---

## 4. The journey of one heartbeat (end to end)

This is the heartbeat of the whole system — trace it once and everything else falls into place.
It repeats every ~15 seconds.

1. **Agent samples the session.** `Program.cs` (Agent) calls `monitor.Sample()` in
   `SessionMonitor.cs`. That asks Windows "how many seconds since the last input?" via
   `NativeMethods.GetIdleSeconds()` and checks a locked/unlocked flag. It returns one of
   `Active`, `Idle`, or `Locked`.

2. **Agent builds a `Heartbeat`** (who I am, my state, my idle seconds) and calls
   `client.ExchangeAsync(hb)` in `ServiceClient.cs`.

3. **Agent connects (or reuses) the pipe** and writes the heartbeat as a JSON line.

4. **Service receives it.** `PipeServer.cs` is sitting in `HandleConnectionAsync`, reads the line,
   deserializes it to a `Heartbeat`, checks the protocol version, and calls
   `_tracker.Process(hb)`.

5. **The decision happens** in `UsageTracker.Process` (the most important method in the project):
   - re-reads `policy.json` (so edits apply live),
   - logs the raw sample to the `activity_log` table (for the parent's reports),
   - if the state is `Active`, works out how many seconds elapsed **since this user's previous
     Active heartbeat**, clamps it, and adds it to today's total in SQLite,
   - looks up this child's rules, checks the bedtime window and the daily limit,
   - decides the `EnforcementAction`: `None`, `Warn`, or `Lock`,
   - returns a `UsageSnapshot` (used minutes, remaining, bedtime?, the action).

6. **Service writes the snapshot back** down the pipe as a JSON line.

7. **Agent acts on it** in `HandleSnapshot`:
   - `Warn` → show a countdown message (currently a console line; a real toast in M2),
   - `Lock` → call `NativeMethods.LockWorkStation()` — the screen locks,
   - `None` → do nothing.

8. **Agent sleeps ~15 seconds** and the loop repeats.

That's the entire system. Everything in section 5 is just the supporting cast for these 8 steps.

---

## 5. Class-by-class reference

### ParentalGuard.Core (the shared vocabulary)

**`Models/ActivityState.cs`** — an `enum` with three values: `Active`, `Idle`, `Locked`. The
Agent decides which one applies; the Service only credits time for `Active` samples.

**`Models/UsageSnapshot.cs`** — the Service's *reply*. A `record` (immutable data holder) carrying
today's used minutes, the limit, remaining minutes (a computed property), whether we're in
bedtime, and the `EnforcementAction` the Agent should perform. Also defines the
**`EnforcementAction`** enum (`None` / `Warn` / `Lock`). The Agent reads this and never computes
any of it itself.

**`Ipc/Heartbeat.cs`** — the Agent's *request*. A `record` with the user name, the activity state,
idle seconds, a timestamp, and a protocol version. One of these is sent every tick.

**`Ipc/IpcContract.cs`** — the shared constants both ends must agree on: the **pipe name**, the
**protocol version**, and the **JSON serializer settings**. Having one `Serialize`/`Deserialize`
helper here guarantees both sides encode identically. Bump `ProtocolVersion` whenever you change
the shape of `Heartbeat` or `UsageSnapshot`, so an old Agent and a new Service refuse to
mis-communicate.

**`Models/PolicyConfig.cs`** — the parent's rules, i.e. the shape of `policy.json`. Three classes:
- `PolicyConfig` — top level: the heartbeat interval, the idle threshold, and a dictionary of
  children keyed by Windows account name. `Default()` produces the first-run file.
- `ChildPolicy` — one child's rules: daily limit, bedtime, warning thresholds, an on/off switch.
- `BedtimeWindow` — a start and end time with a `Contains(now)` method that correctly handles a
  window that **crosses midnight** (e.g. 21:00 → 07:00). Worth reading — it's a nice small piece
  of logic.

### ParentalGuard.Service (the brain)

**`ServicePaths.cs`** — one place that defines every on-disk path, all under
`C:\ProgramData\ParentalGuard`. `EnsureCreated()` makes the folders at startup. Centralising this
means there are no magic path strings scattered around.

**`Data/UsageStore.cs`** — the SQLite layer. Opens one connection for the process lifetime and
creates two tables on first run:
- `usage_daily` — one row per (user, day) holding accumulated **active seconds**. This is what
  survives reboots, so a restart can't reset the day.
- `activity_log` — an append-only log of every sample, for the parent's future reports.

Key methods: `AddActiveSeconds` (UPSERT that adds time and returns the new total in minutes),
`GetUsedMinutes`, and `LogSample`. It uses a `lock` so the single pipe loop can't trip over
itself. (SQLite "WAL mode" is switched on for safe concurrent reads later — that's the
`.db-wal`/`.db-shm` sidecar files you saw.)

**`ConfigStore.cs`** — loads/saves `policy.json`. On first run it writes the defaults so the parent
has a file to edit. `Load()` is called **on every heartbeat**, which is *why editing the JSON takes
effect immediately with no restart* (you saw this when you changed the limit to 1). `Save()` writes
to a temp file then renames, so a reader never catches a half-written file. If the file is corrupt,
it falls back to defaults rather than crashing — enforcement must never go offline.

**`UsageTracker.cs`** — the decision engine; `Process(Heartbeat)` is the heart of the app
(walked through in section 4, step 5). Two subtle bits worth studying:
- **Elapsed-gap counting with a clamp.** Instead of assuming "a heartbeat = 15s of use", it
  measures the real time since the user's previous *Active* heartbeat and clamps it to at most
  2× the interval. This means a late/missed heartbeat can't be gamed for free minutes, and a
  flood of heartbeats can't inflate the count.
- **`DecideAction`** — the pure rules function: bedtime and over-limit both mean `Lock`;
  approaching the limit means `Warn`; otherwise `None`. Pure and easy to unit-test later.

**`Ipc/PipeServer.cs`** — creates the named pipe **with an ACL** (authenticated local users may
connect; SYSTEM has full control), waits for a connection, and in `HandleConnectionAsync` reads
heartbeats line by line, calls the tracker, and writes snapshots back. Wrapped in a resilient
loop so one bad connection can't bring the server down.

**`Workers/IpcWorker.cs`** — the `BackgroundService` that hosts the pipe server for the host's
lifetime (section 3b).

**`Program.cs`** — wiring. Creates the host, registers it as a Windows Service, registers the
singletons (`UsageStore`, `ConfigStore`, `UsageTracker`, `PipeServer`) in DI, adds the
`IpcWorker`, touches the config once to write the first-run file, and runs.

### ParentalGuard.Agent (the reporter — runs in the child's session)

**`NativeMethods.cs`** — the only place that calls into Windows (Win32) directly, kept small on
purpose so it's easy to audit. Three calls: `GetLastInputInfo` + `GetTickCount64` (to compute idle
seconds) and `LockWorkStation`. Uses the modern `[LibraryImport]` source generator (this is why the
`.csproj` needs `AllowUnsafeBlocks` — the generator emits marshalling code that's technically
"unsafe" C#).

**`SessionMonitor.cs`** — turns the raw Win32 data into an `ActivityState`. It subscribes to
`SystemEvents.SessionSwitch` to know when the session is locked/unlocked, and compares idle seconds
against the threshold to decide `Active` vs `Idle`. (This is the class that needed the
`Microsoft.Win32.SystemEvents` NuGet package.)

**`ServiceClient.cs`** — the Agent's end of the pipe. `ExchangeAsync` connects (or reuses the
connection), sends a `Heartbeat`, and reads back a `UsageSnapshot`. If the Service is down
(restarting), it quietly returns `null` and retries next tick — the session stays usable.

**`Program.cs`** — the Agent's loop (section 4). Sample → exchange → act on the snapshot → sleep.
`HandleSnapshot` contains the warn/lock behaviour, with a little bookkeeping so it doesn't show the
same warning every single tick.

---

## 6. Design decisions worth internalising

- **The Agent has zero authority.** All counting and all decisions live in the protected Service.
  Killing the Agent can only *lose* you screen time, never gain it.
- **Config is re-read every heartbeat.** Live edits, no restart — and later the web panel just
  writes the same file.
- **Time is counted from real elapsed gaps, clamped.** Resistant to stalled/flooded/clock-tampered
  heartbeats.
- **State persists in SQLite under an admin-only folder.** Reboots don't reset the day; a standard
  user can't edit the totals or the rules.
- **Protection comes from OS permissions, not secrecy.** That's why the whole thing can be open
  source: the child reading the code doesn't help them, because they're a standard user.

---

## 7. Suggested reading order in Rider

1. `Core/Ipc/Heartbeat.cs` and `Core/Models/UsageSnapshot.cs` — the two messages. Start here.
2. `Core/Models/PolicyConfig.cs` — the rules. Read `BedtimeWindow.Contains`.
3. `Agent/Program.cs` — see the loop that drives everything.
4. `Service/UsageTracker.cs` — the brain. Read `Process` then `DecideAction`.
5. `Service/Data/UsageStore.cs` — where the numbers live.
6. `Service/Ipc/PipeServer.cs` + `Agent/ServiceClient.cs` — the two ends of the pipe, side by side.
7. `Service/Program.cs` — how it's all wired together.

---

## 8. Things to try (to make it stick)

- Put a breakpoint in `UsageTracker.Process` and watch a live heartbeat arrive. Inspect `hb` and
  the `UsageSnapshot` you return.
- Change `HeartbeatSeconds` to `5` in `policy.json` and watch the loop speed up with no restart.
- Set a `Bedtime` window that includes *right now* and confirm it locks even with time remaining.
- Open `usage.db` with **DB Browser for SQLite** (while the Service is stopped) and look at the
  `usage_daily` and `activity_log` rows you've been generating.
- Kill the Agent from Task Manager and confirm the Service stops crediting time (the `usage_daily`
  total stops rising) — proof that killing the Agent buys nothing.

When you've digested this, we pick up at M2 (tray UI) or M3 (DNS filter).
