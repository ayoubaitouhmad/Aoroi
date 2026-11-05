# ParentalGuard

An open-source, network-and-device parental control tool for Windows. Built to run on your
own family PCs: it tracks how long a child account is actively used, enforces a daily limit
and a bedtime window, and (in later milestones) filters web content via a local DNS layer —
all with a dashboard you can open from your phone on your home Wi-Fi. No cloud account, no
external backend: your data stays on your machine.

> **Status: Milestone 1 — usage tracking + limit/bedtime enforcement (foundation).**
> DNS filtering, the web panel, bypass-proofing hardening, and the profanity extension are
> planned next. See the roadmap below.

## Why two programs

Windows runs services in "session 0", isolated from any user's desktop. A service there
**cannot** read a user's keyboard/mouse idle time and **cannot** show a window. So the design
splits in two:

| Component | Runs as | Job |
|-----------|---------|-----|
| **ParentalGuard.Service** | Windows Service, `LocalSystem` | The protected brain. Counts active time, stores it in SQLite, decides when to warn/lock, serves the (future) web panel. A standard user can't stop it. |
| **ParentalGuard.Agent**   | The child's own session    | The dumb reporter. Samples idle/lock state, heartbeats the Service, and performs the lock the Service orders. If the child kills it, time simply stops being credited — no free screen time. |

They talk over a **local named pipe** (never the network), with an ACL that only lets
authenticated local users connect.

```
Child session                         Session 0 (SYSTEM)
┌───────────────────┐   heartbeat     ┌────────────────────────────┐
│ ParentalGuard.Agent├───────────────►│ ParentalGuard.Service       │
│  • idle/lock sample│                │  • UsageTracker (decide)    │
│  • show warnings   │◄───────────────┤  • UsageStore (SQLite)      │
│  • lock on command │   snapshot      │  • ConfigStore (policy.json)│
└───────────────────┘                 └────────────────────────────┘
```

## Project layout

```
ParentalGuard/
├─ ParentalGuard.sln
├─ global.json                      # pins the .NET 10 SDK
└─ src/
   ├─ ParentalGuard.Core/           # shared models + IPC contract (no logic)
   │  ├─ Models/                    # PolicyConfig, UsageSnapshot, ActivityState, …
   │  └─ Ipc/                       # Heartbeat, IpcContract
   ├─ ParentalGuard.Service/        # the brain
   │  ├─ Data/UsageStore.cs         # SQLite persistence
   │  ├─ ConfigStore.cs             # policy.json load/save
   │  ├─ UsageTracker.cs            # counting + warn/lock decision
   │  ├─ Ipc/PipeServer.cs          # named-pipe listener
   │  └─ Workers/IpcWorker.cs       # hosted background loop
   └─ ParentalGuard.Agent/          # the reporter (child session)
      ├─ NativeMethods.cs           # the few Win32 calls, in one auditable file
      ├─ SessionMonitor.cs          # idle + lock detection
      └─ ServiceClient.cs           # pipe client
```

## Requirements

- Windows 10 (21H2+) or Windows 11
- .NET 10 SDK
- Visual Studio 2026 (Community is fine) or `dotnet` CLI

## Build

```powershell
dotnet build
```

## Try it (developer loop — run inside a throwaway VM, never your own PC)

Open **two** terminals.

1. **Service** (run this terminal *as Administrator* so it can write to `%ProgramData%`):
   ```powershell
   dotnet run --project src/ParentalGuard.Service
   ```
   On first run it creates `C:\ProgramData\ParentalGuard\policy.json` and `usage.db`.
   Open `policy.json` and set a tiny limit (e.g. `"DailyLimitMinutes": 1`) to see enforcement fast.

2. **Agent** (normal terminal, as the test "child" user):
   ```powershell
   dotnet run --project src/ParentalGuard.Agent
   ```
   Keep moving the mouse; watch the Service credit active minutes and, once the limit is hit,
   order the Agent to lock the workstation.

## Install as a real Windows Service

See `tools/install-service.ps1` (run elevated). It publishes the Service and registers it to
start at boot as `LocalSystem`. The Agent is launched per-session (a scheduled task at logon is
set up in a later milestone).

## Configuration (`policy.json`)

```jsonc
{
  "HeartbeatSeconds": 15,
  "IdleThresholdSeconds": 120,
  "Children": {
    "child": {                       // Windows account name (case-insensitive)
      "DailyLimitMinutes": 120,
      "Bedtime": { "Start": "21:00", "End": "07:00" },   // may cross midnight
      "WarnAtMinutes": [10, 5, 1],
      "EnforcementEnabled": true
    }
  }
}
```

## How counting resists tampering

- Time is credited from the **elapsed gap between Active heartbeats**, clamped to at most
  2× the interval — a stalled or flooded Agent can't mint minutes.
- Totals live in SQLite under `%ProgramData%` (admin-only), so a **reboot doesn't reset** the day.
- Idle beyond the threshold and locked sessions **stop the clock**.
- The **child account must be Standard, not Administrator** — this is the single most important
  setting. An administrator can stop any software on Windows, including this one.

## Roadmap

- [x] M1 — Usage tracking, daily limit, bedtime lock
- [x] M2 — Tray UI (tray icon, balloon warnings, countdown + time's-up overlays) and a logon
      scheduled task that auto-starts and restarts the Agent. *Known gap:* instant relaunch
      after a deliberate kill is deferred to the hardening milestone (Service-side
      `CreateProcessAsUser` + logoff fallback); until then, killing the Agent only stops time
      being credited, it doesn't grant screen time.
- [ ] M3 — Local DNS filter: category blocklists, forced SafeSearch / YouTube Restricted Mode
- [ ] M4 — Web panel on the home network (view usage + sites, edit policy), with login
- [ ] M5 — Bypass-proofing: disable DoH via policy, block VPN/proxy domains, Safe Mode handling
- [ ] M6 — Profanity: force-installed browser extension that blurs flagged words
- [ ] M7 — Parent-friendly installer (WiX/Inno) that guides the admin/standard account setup

## Design principles (so it stays a *parental* tool, not spyware)

- Visible by design: the child sees a tray icon and a notice on their account.
- No keylogging, no screenshots, no reading messages.
- Protection comes from OS permissions, not from hiding — the code is fully open.
- Report bypasses privately: see `SECURITY.md`.

## License

GPL-3.0 (see `LICENSE`) — improvements that get distributed stay open for other families.
Category blocklists are downloaded from their upstream sources at runtime and credited;
they are **not** bundled, to respect their individual licenses.
