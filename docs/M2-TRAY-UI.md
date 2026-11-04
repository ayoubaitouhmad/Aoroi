# M2 — Tray UI & Auto-Start (Code Walkthrough)

This document explains the **second milestone** of ParentalGuard: turning the Agent from a bare
console loop into a real, child-facing **system-tray application**, and making it **start by
itself** when the child logs in. It assumes you've read `ARCHITECTURE.md` (the M1 walkthrough).

Written for a developer who knows **WinForms** and **ASP.NET** but is newer to Windows services
and desktop-agent patterns.

---

## 1. What M2 is trying to achieve

After M1 we had a working brain (the Service) and a working reporter (the Agent), but the Agent
was a **console app**: warnings were `Console.WriteLine` lines nobody would ever see, and if the
PC rebooted, nothing started it again. M2 fixes the *human* side of the tool:

1. **A visible presence** — a tray icon the child (and you) can see, so the tool is honest and
   not hidden spyware.
2. **Warnings the child actually notices** — a balloon notification and an on-screen countdown
   as the daily limit approaches.
3. **A clear "time's up" moment** — a full-screen screen that explains *why* the PC locked
   (limit reached vs bedtime), instead of a silent lock.
4. **It starts itself** — a logon scheduled task launches the Agent automatically and restarts
   it if it exits, so you don't have to start it by hand.

What M2 deliberately does **not** change: the Service, the counting, the IPC contract, or any
decision logic. The Agent is still "dumb" — it reports and obeys. M2 is purely about *presenting*
the Service's decisions and *staying alive*.

---

## 2. The shape of the change

```
BEFORE (M1)                              AFTER (M2)
ParentalGuard.Agent (console .exe)       ParentalGuard.Agent (WinForms .exe, WinExe)
└─ Program.cs                            ├─ Program.cs                  (STA entry point)
   • while-loop                          ├─ AgentApplicationContext.cs  (tray + loop)
   • Console.WriteLine warnings          ├─ Ui/CountdownOverlay.cs      ("X min left" banner)
                                         ├─ Ui/TimesUpOverlay.cs        (full-screen lock screen)
                                         └─ (NativeMethods / SessionMonitor / ServiceClient
                                             unchanged from M1)

tools/
└─ install-service.ps1                   └─ install-agent-task.ps1      (logon auto-start)
```

Three project settings flipped in `ParentalGuard.Agent.csproj`:

| Setting | M1 | M2 | Why |
|---------|----|----|-----|
| `OutputType` | `Exe` | `WinExe` | `WinExe` means **no console window** flashes up in the child's session. |
| `UseWindowsForms` | `false` | `true` | Brings in `NotifyIcon`, `Form`, and the implicit `System.Windows.Forms` / `System.Drawing` usings. |
| `AllowUnsafeBlocks` | (added mid-M1) | kept | Needed by the `[LibraryImport]` source generator in `NativeMethods.cs`. |

---

## 3. Step-by-step through the code

### Step 1 — The entry point becomes STA (`Program.cs`)

WinForms requires its UI thread to be a **single-threaded apartment (STA)** — a COM requirement
that the tray icon and windows depend on. Top-level statements don't let us attach `[STAThread]`,
so M2 uses an explicit `Main`:

```csharp
[STAThread]
private static void Main()
{
    ApplicationConfiguration.Initialize();          // DPI + default font (SDK-generated)
    Application.Run(new AgentApplicationContext());  // pumps the Windows message loop
}
```

`Application.Run(ApplicationContext)` is the standard pattern for a **tray app with no main
window**. The message loop runs (so the tray icon, balloons and overlays work), but nothing opens
on screen. The app lives until the context exits.

### Step 2 — The tray context owns everything (`AgentApplicationContext.cs`)

This class is the M1 loop reorganised into an object that also holds the UI. In its constructor it:

1. builds a `ContextMenuStrip` with a single **disabled** status line — and crucially **no "Exit"
   item**, so the child can't shut the Agent down from the menu;
2. creates the `NotifyIcon` (the tray icon) using `SystemIcons.Shield` so we don't have to ship a
   binary `.ico`;
3. constructs the two overlay forms (hidden);
4. starts the heartbeat loop with `_ = RunLoopAsync(_cts.Token);`.

The loop body is unchanged in spirit from M1 — sample → send heartbeat → receive snapshot → act →
sleep 15s — but "act" now drives the UI in `HandleSnapshot`:

- **`Warn`** → hide the time's-up screen, show/update the countdown banner, and pop a balloon
  *only when the remaining-minutes number changes* (so it nudges once per threshold, not every
  15 seconds).
- **`Lock`** → hide the countdown, show the full-screen screen with the reason, then call
  `NativeMethods.LockWorkStation()`.
- **`None`** → hide both overlays and reset the warning memory.

### Step 3 — The countdown banner (`Ui/CountdownOverlay.cs`)

A small borderless `Form` pinned top-right. The WinForms techniques that make it behave like an
overlay rather than a normal window:

- `ShowWithoutActivation => true` — it never steals keyboard focus, so it won't interrupt typing.
- A `CreateParams` override adds two extended window styles:
  `WS_EX_TOOLWINDOW` (keeps it out of Alt+Tab) and `WS_EX_NOACTIVATE` (never becomes active).
- `OnFormClosing` cancels any `UserClosing` attempt — the child can't close it; only the Agent
  hides it.

### Step 4 — The time's-up screen (`Ui/TimesUpOverlay.cs`)

A full-screen `Form` sized to `SystemInformation.VirtualScreen` (covers **all** monitors), black
at 92% opacity, with a big centered message. It blocks closing two ways: `CS_NOCLOSE` disables the
window's close box, and `OnFormClosing` cancels Alt+F4's `WM_CLOSE`. It's shown *with* the
workstation lock, so after the child unlocks they see why — and if they're still over the limit,
the next heartbeat re-locks.

### Step 5 — Auto-start (`tools/install-agent-task.ps1`)

Publishes the Agent to `Program Files\ParentalGuard\Agent` and registers a **per-child scheduled
task** that:

- triggers **at that child's logon** (`New-ScheduledTaskTrigger -AtLogOn -User child`),
- runs in the child's session as a standard user (`-RunLevel Limited`),
- restarts if it exits and is never time-limited (`-RestartCount 999`, `ExecutionTimeLimit 0`).

Run it elevated as `.\install-agent-task.ps1 child`.

---

## 4. The technical logic worth understanding

### 4a. Why `ApplicationContext` instead of a main form

A tray utility has no primary window. If you `Application.Run(new SomeForm())`, closing that form
ends the app. `ApplicationContext` gives you the message loop **without** tying the app's lifetime
to a visible window — the right model for something that lives in the tray.

### 4b. The threading model (the important one)

The heartbeat loop is async (it awaits the named pipe) but it must update WinForms controls, and
**WinForms controls may only be touched from the UI thread**. The naive fix is `Control.Invoke`
everywhere. We avoid that with one deliberate choice:

> Start the loop **on the UI thread** and never use `ConfigureAwait(false)`.

Because `Application.Run` installs a `WindowsFormsSynchronizationContext`, every `await`
continuation is posted **back to the UI thread**. So after `await _client.ExchangeAsync(...)`, we
are *already* on the UI thread when `HandleSnapshot` runs — it can touch the tray icon and the
overlay forms directly, no `Invoke` needed. And because `ExchangeAsync` is genuinely async (it
awaits `ConnectAsync` / `ReadLineAsync`), the UI thread is free while we wait on the pipe, so the
app never freezes.

This is the same `SynchronizationContext` idea you've met in ASP.NET; here it's doing the thread
marshalling for us for free. It's a clean pattern to reuse any time you mix an async loop with a
desktop UI.

### 4c. Presentation vs enforcement (a security distinction)

The overlays are **presentation** — they explain and discourage. The actual enforcement is
`LockWorkStation()`. This separation matters: a standard-user child could Alt+Tab under an overlay
or kill the Agent, so we never *rely* on the overlay to keep them out. The real wall is the OS
lock, and (later) a Service-side fallback. Don't confuse "looks blocked" with "is blocked".

### 4d. The honest gap in M2's "stays alive"

The logon task covers **reboot, logon, and crashes**. It does **not** guarantee instant relaunch
if the child *deliberately* kills the Agent from Task Manager. That matters because the Agent is
what performs the lock — kill it before the limit and no lock fires.

Two things keep this from being a disaster, and one milestone fixes it properly:

- Killing the Agent **after** the limit buys nothing: the Service stops crediting time the moment
  heartbeats stop, so there's no way to *earn* screen time by killing it.
- The child is a **standard user**, so they can't stop the Service or change the schedule.
- The proper fix (planned for the **M5 hardening** milestone): the Service — which the child
  can't touch — spawns the Agent into the session with `CreateProcessAsUser`, and falls back to
  `WTSLogoffSession` if the Agent can't be kept alive. That closes the gap without depending on a
  cooperative Agent.

This is called out in the code (`TimesUpOverlay` header) and the README roadmap so it isn't
forgotten.

---

## 5. How to test M2 in the VM

Because the Agent is now `WinExe`, `dotnet run --project src/ParentalGuard.Agent` **won't print to
the console** — watch the tray instead:

1. Start the Service (elevated) as before.
2. Start the Agent. A shield icon appears in the tray; hover it to see "X min left".
3. Set a small limit in `policy.json` (and an `ayoub`/your-user block). As it approaches, the
   countdown banner appears top-right and a balloon pops.
4. At the limit, the full-screen "time's up" screen shows and the VM locks.
5. Optional: run `tools/install-agent-task.ps1 <user>` elevated, sign out and back in, and confirm
   the Agent starts on its own.

Roll back your VM checkpoint between runs to start clean.
