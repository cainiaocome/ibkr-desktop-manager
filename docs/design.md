# IBKR Desktop Manager — Design

A small Windows GUI utility that starts, monitors and stops the **IBKR Desktop** app,
and optionally auto-fills the login form with credentials from environment variables,
Windows Credential Manager, or an in-app setup dialog.

---

## 1. Scope

**In scope**

- Detect whether IBKR Desktop is installed / running / which version, with PID + uptime.
- Report whether the **selected account** has a username and a password (never display the values),
  and which account is selected.
- Start / graceful quit / force kill / restart the app.
- **Auto Login button**: user-triggered — launches the app if needed and fills the login form;
  refuses with a visible error when no credentials are available.
- **Accounts**: named credential pairs, add/edit/delete in-app, persisted securely (§7.1), one marked
  as the startup default and switchable from the main window (§7.2).
- Release pipeline: build + test + portable zip artifacts on GitHub Actions.

**Out of scope (v1)**

- macOS/Linux (see §3 for the seam that keeps this cheap).
- Multi-account / multi-profile switching (single account only, see §11).
- Trading/order automation, TWS/Gateway support, API socket management.
- 2FA bypass, session/keepalive scheduling.
- Installers (portable zip only); auto-update (Velopack is a later option).

---

## 2. Facts about IBKR Desktop that shape the design

Verified from public sources; a **day-1 spike must confirm them** (see §10).

Measured on a real installation by the `IBKR Desktop probe` workflow (full detail in
`docs/ibkr-app-notes.md`).

| Fact | Consequence |
|---|---|
| Installer is install4j; `-q -dir C:\ntws -overwrite` installs unattended; registry `DisplayName="IBKR Desktop"`, `InstallLocation="C:\ntws"`, version `latest (4.0d) …` | Resolve from the registry uninstall keys; CI can install the real app |
| Launcher **`ntws.exe`**, 64-bit, runs the app in-process (no `java.exe` child) | Kill the process **tree** anyway — the app spawns helpers |
| The UI is **Qt 6.8 QML + CEF**, window class `Qt683QWindowIcon`, and the top-level title is **always `IBKR Desktop`** | Title-based classification can never distinguish login from logged-in; use accessibility content |
| UIA exposes `QApplication.launcherLoginWindow` with two `TextField_QMLTYPE_*` edits (with screen rectangles) and a `Log In` button; `ValuePattern.SetValue` reports success but the QML form ignores it | Accessibility is used for *identification and geometry*, never to write values; the fields are clicked and typed into |
| Login frequently requires 2FA/security-code | Auto-login stops at "awaiting 2FA"; a human finishes. GUI must say so |
| App self-updates and renames QML objects between versions | All matching is profile-driven and fail-soft; the probe workflow turns red when ids change |

---

## 3. Tech stack

**Decision: C# / .NET 8 (LTS) + Avalonia 11, MVVM, published as a self-contained `win-x64` exe.**

Implemented as planned, with one deliberate tweak: every project targets `net8.0` (not
`net8.0-windows`) and Windows-only code is annotated `[SupportedOSPlatform("windows")]` + guarded at
the entry point. The whole solution therefore builds and unit-tests on Linux/macOS, while the
`windows-latest` CI job exercises the real Win32 paths.

Why:

- The hard part is Win32 + UI Automation, and .NET has the best-supported interop there
  (`FlaUI.UIA3`, raw `SendInput`, Job Objects, `Process.Kill(entireProcessTree)`).
- One language for core, platform layer, GUI and tests; no Node/Python runtime to ship.
- Avalonia gives a real GUI today and makes the eventual macOS port a **platform-layer swap
  only** — no GUI rewrite (the requirement explicitly allows macOS later).
- `dotnet publish -r win-x64 --self-contained` → one exe, no installer needed, trivial CI on `windows-latest`.
- Testability: state machine + orchestrator live in a `netstandard`-ish library with no Windows calls.

Alternatives considered: WPF/WinUI (best Windows fit, GUI rewrite for macOS later),
Rust+Tauri (tiny, immature UI-automation crates), Electron/Node (100 MB+, weak UIA),
Python+PySide6+pywinauto (fastest prototype, huge bundle, AV false positives),
Go+Wails (weak accessibility bindings). Cost of being wrong is low: the core and all
platform interfaces are UI-framework agnostic.

---

## 4. Architecture

```
┌──────────────────────────── IbkrManager.App (Avalonia, MVVM) ────────────────────────────┐
│ StatusView · LogView · SettingsView · TrayIcon                                           │
└───────────────────────────────────────┬──────────────────────────────────────────────────┘
                                        │ IAppManager (commands + IObservable<AppState>)
┌───────────────────────────────────────▼──────────────────────────────────────────────────┐
│ IbkrManager.Core  (portable, fully unit-tested)                                          │
│  AppStateMachine · LaunchOrchestrator · LoginCoordinator · Diagnostics · ISecretProvider │
│  ── interfaces ──  IAppLocator · IProcessController · ILoginDriver · IClock · ILog       │
└───────────────────────────────────────┬──────────────────────────────────────────────────┘
┌───────────────────────────────────────▼──────────────────────────────────────────────────┐
│ IbkrManager.Platform.Windows                                                             │
│  RegistryAppLocator · Win32ProcessController (Job Object) · UiaLoginDriver               │
│  EnvSecretProvider (+ CredentialManager provider) · InstanceGuard (named mutex)          │
└──────────────────────────────────────────────────────────────────────────────────────────┘
     IbkrManager.Platform.Mac  ← later: `open -a`, NSWorkspace, AXUIElement
```

Key rules:

- `Core` never P/Invokes; every OS touch goes through an interface (that is the macOS seam).
- The GUI is a dumb renderer of `AppState`; it issues commands, never touches processes.
- All app-specific knobs (exe path, window titles, element names, timings) live in
  `app-profile.json`, shipped as a resource and overridable in settings — a new app
  version becomes a config change, not a code change.

### Status model (deliberately flat)

```
Unknown ──▶ NotInstalled | Stopped ◀──Quit/exit── Stopping ◀──Quit── Running ◀──Start/Launch──
                                └───────────── error ──▶ Failed
```

`Running` means "the process exists" — not "logged in". The UI additionally shows the outcome of the
last Auto Login (`LoginAttempt`: submitted, awaiting 2FA, or failed, with a timestamp), which is a
record of an action, not a tracked state.

The 2 s poll only reads process state (`IProcessController`), never the app's window or accessibility
tree. There is no `Launching`/`AwaitingLogin`/`Awaiting2FA`/`LoggedIn` state machine: keeping one alive
means inspecting another process's UI on every tick, which is expensive for that process (it runs the
UIA queries on its own UI thread) and gives the manager nothing it can act on.

**Start** and **Auto Login** are commands. Auto Login waits for the login form *inside the call*, fills
it, waits for IBKR's answer, records the outcome and returns. Nothing is injected without that click.

---

## 5. Launch & login flow

1. **Resolve install**: registry uninstall keys (`DisplayName` ≈ *IBKR Desktop*) → exe path;
   fall back to known paths; user override wins. Report "not installed" with a download link.
2. **Guard**: named mutex for single instance; refuse to start a second copy of the app
   (`ntws.exe` already running → attach instead of launching).
3. **Start**: `Process.Start` with `UseShellExecute=false`, working dir = install dir,
   the process **assigned to a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`** so an
   orphaned app dies with the manager. Credentials never go into argv (visible in the process
   list) and the password is never added to the child's environment.
4. **Wait for login window** (timeout ~120 s, configurable), by window class/title from the profile.

**Design rule: the manager is stateless about the app's UI.** It knows whether the executable is
installed and whether the process is running, and it records the outcome of the last Auto Login. It
does *not* keep a live login/2FA/logged-in state: that needs a window inspection on every tick, which
runs inside the app's UI thread and gave the real app a measurable slow-down (see
`docs/ibkr-app-notes.md`). All UI inspection is scoped to the Auto Login action.

**Auto Login** (only on button click) — validation first:

- **Auto Login** validation uses the resolved credential (not just the env vars): missing → abort
  before launching, log which field is missing, show an inline error banner. Never prompt, never
  guess.
- Both present → ensure the app is running (launch if `Stopped`), wait for the login window if needed.
- Then run the injection ladder, first *verified* success wins:
  1. `pointer` — UI Automation locates the fields and their screen rectangles. On an interactive
     desktop the manager clicks them with real mouse events and types with `SendInput`; otherwise it
     posts the same clicks/characters as window messages, which the app processes in its own loop
     (this is what makes auto-login work in a service session or against an elevated app).
     After typing it reads the username back, requires the password field to report characters, and
     requires the username to still be intact — a form that merely *looks* filled is the failure mode
     this driver exists to prevent.
  2. `uia` — `ValuePattern.SetValue` + `InvokePattern`; kept for toolkits that propagate values.
  3. `sendinput` — blind tab-order typing, no verification.
  4. `manual` — skip injection, tell the user to type it themselves (fallback, not default).
- Then **wait for IBKR's verdict**: the form is replaced by "Connecting to server…", so a missing
  form is not success. The manager reports the app's own message (or its refusal dialog), a 2FA
  screen, or a timeout. The wait takes one accessibility snapshot per second, requires the form-less
  view to settle, and ends when the click ends.
- **Stay out of the app's way afterwards.** Accessibility queries run inside the app's UI thread, so
  the inspector walks the tree breadth-first with a depth/node budget and inspects only login windows
  deeply. Nothing inspects the app between clicks.
5. **Stop at 2FA**: never attempt to solve it. Report "awaiting 2FA" in the outcome of the click; the
   manager does not monitor the app afterwards.
6. **Quit** (`ICloseConfirmationPrompt`): post `WM_CLOSE` → wait `GracefulShutdownTimeout`; if the app
   is still there, look for its "really quit?" dialog and press the confirming button (a window-targeted
   click, then wait again). **Never** escalate to `TerminateProcess` — the app may still be flushing
   state, and killing a logged-in session is exactly what the user asked us to stop doing. Staying open
   is reported as such; `Force Quit` is the explicit termination, and `Restart` refuses rather than
   kill. Measured on the real app: at the login screen `WM_CLOSE` exits in ~1.5 s with no dialog, so the
   confirmation path only runs on a logged-in session (not reproducible in CI without an account).

Failure policy: any injection step fails → log the reason, show an error banner, leave the app alone so
the user can finish manually. Never retry-loop credentials (lockout risk).

---

### Shutdown behaviour

The manager must never be the reason a machine cannot shut down. Windows' protocol is: send
`WM_QUERYENDSESSION` to every top-level window, wait for each answer, and if an app does not answer in
time mark it *not responding* and put up a screen that needs someone at the console to continue. On a
headless remote machine that is a shutdown that never finishes.

Therefore:

- `ShutdownHook` subclasses the main window, answers `WM_QUERYENDSESSION` with **allow** before doing
  anything else, and on `WM_ENDSESSION` stops the timer and cancels in-flight commands.
- The Avalonia UI thread never runs manager work: ticks and commands are dispatched with `Task.Run`,
  so the message pump is free even if a Win32 call or an accessibility query hangs.
- The kill-on-close Job Object means the app dies with the manager, so a hung IBKR Desktop cannot hold
  the session open either.
- Logging flushes every line, so a forced termination still leaves the last actions on disk.

## 6. GUI

Single 480×360 window, shown on launch (window-first, no start-minimized mode).

```
IBKR Desktop Manager                                   [–][□][×]
──────────────────────────────────────────────────────────────
  IBKR Desktop      ● Running        PID 8420 · 00:12:31
  Version           0.12.0 (installed: 0.11.2)
  Account           [ Main ▾ ]  [ Manage… ]   username ✓  password ✓  (stored)
  Login state       ● Awaiting 2FA — finish in the app window
──────────────────────────────────────────────────────────────
  [ Start ] [ Auto Login ] [ Quit ] [ Force Quit ] [ Restart ]   [ Settings ] [ Logs ]
──────────────────────────────────────────────────────────────
  ⚠ Auto Login blocked: IBKR_PASSWORD is not set
  last: autologin typed username, waiting for password field
```

- Buttons: `Start` (launch, stop at login screen), `Auto Login` (launch if needed + inject),
  `Quit`, `Restart`. `Auto Login` stays clickable so a missing credential produces an explicit
  inline error rather than a mystery-disabled button; a hint sits next to it when a var is absent.
- The account picker + `Manage…` open the dialog in §7.2 (add / edit / delete / choose).
- Rows use green/amber/red dots; credentials row shows presence + source (`env`), never
  masked values (a mask still leaks length).
- `Start`/`Auto Login` disabled unless the app is installed; `Quit`, `Force Quit` and `Restart` are
  disabled when stopped.
- `Quit` is graceful by design and may end with the app still running (if it refuses to close);
  `Force Quit` is the destructive one and says so in its tooltip.
- Window-first; there is no tray icon in v0.1.0 (the window is the app), and closing the window
  exits the app — no close-to-tray surprise.
- Settings: exe path override, env var names, login driver, timeouts, start-with-Windows.
- Toasts only for terminal states (`Failed`, `Awaiting2FA`).

---

## 7. Security & privacy

- Secrets: `Secret` wrapper type whose `ToString()`/`DebuggerDisplay` is `***`; `ILog` sinks are
  filtered to never receive it. No secrets in argv, logs, temp files, crash dumps, telemetry.
- Zero out unmanaged buffers where practical; keep secrets out of `settings.json` (it holds aliases
  and account ids only).
- The password is never placed in the launched app's environment — injection is UI-only, and
  environment variables are no longer a credential source at all.
- No network calls in v1 except an optional read-only version check (off by default).
- README must state clearly: this automates a UI, it is not sanctioned by IBKR, 2FA is never
  bypassed, and credentials are your responsibility. MIT-licensed, no warranty.

### 7.1 Accounts (credential profiles)

An **account** is an alias plus a credential pair:

| Part | Where it lives | At rest |
|---|---|---|
| `id`, `alias` | `settings.json` (`accounts[]`) | plaintext, not secret |
| username, password | Windows Credential Manager, target `IbkrDesktopManager/{id}/{kind}` | DPAPI, per user |
| a pair typed this session | memory (`SessionCredentialStore`, same target key) | nowhere |

```csharp
public sealed record AccountProfile { string Id; string Alias; }

public sealed record AppSettings
{
    IReadOnlyList<AccountProfile> Accounts;
    string? SelectedAccountId;   // the account Auto Login uses, and the default at startup
}
```

**A profile is a source, not a preference.** Resolution reads *both* halves of the selected account
and never mixes: the 0.3.x provider chain could pair account A's username with account B's password,
which is a wrong-account login. The in-memory store is an overlay for the *same* account id (typed
this session wins over what is stored for that account), never across accounts.

Environment variables are **not** read at all. They were an inherited, plaintext-in-the-registry
channel that also made "which credentials am I using?" ambiguous; `AccountService` replaced them, and
`IBKR_USERNAME`/`IBKR_PASSWORD` now do nothing.

Selection rules, all in `AppSettings.Normalize()` / `AccountService`:

- no accounts → `Resolve()` returns an explicit empty pair and Auto Login says
  "no account is selected. Add one in Manage accounts."
- dangling `SelectedAccountId` → first account
- blank alias → `Account N`; duplicate ids dropped; duplicate aliases rejected on save
- deleting the selected account selects the next one

Migration (once, when the account list is empty): a pre-0.4 pair at
`IbkrDesktopManager/username|password` is **copied** into a new account called *Default*, which is
then selected. The legacy entries are left in place so a downgrade still finds them.

### 7.2 The accounts dialog

An in-app Avalonia modal, not a system window (Windows has no per-user credential UI the manager can
drive):

```
Accounts
  ● Main    · in use        stored · username ✓ · password ✓     [ New account ]
  ○ Paper                   stored · username ✓ · password ✓     [ Use for Auto Login ]
  ○ Client A                this session · username ✓ · password ✗ [ Delete ]
─────────────────────────────────────────────────────────────────────────
Edit "Main"
  Name      [ Main                    ]
  Username  [ alice                   ]      shown: it identifies which account this is
  Password  [ •••••••••  (stored) 👁 ]        never pre-filled; blank means "keep the stored one"
  ☑ Remember in Windows Credential Manager
  [ Close ]                                                 [ Save ]
```

- Renaming does not touch the secret; editing one field keeps the stored other half.
- `Delete` needs a second click (`Really delete?`), then removes the secret and reselects.
- The main window's `Account` combo is two-way: changing it persists the selection (the startup
  default). It never types, launches, restarts or re-logs-in — switching a live session is
  `Quit`, then `Auto Login`.
- No password is ever rendered, logged, put in argv or in the launched app's environment.

---

## 8. Repository layout

```
src/IbkrManager.Core/            state machine, orchestrator, abstractions, app-profile model
src/IbkrManager.Platform.Windows/ registry, Win32, Job Object, FlaUI login driver,
                                 env/credential-manager secret providers, dialog-backed store
src/IbkrManager.App/             Avalonia GUI (MVVM via CommunityToolkit.Mvvm), app icon, tray
tests/IbkrManager.Core.Tests/    xUnit: state machine, profile parsing, redaction, timeouts
tests/IbkrManager.Platform.Windows.Tests/  xUnit: locator (mocked registry), process tree
build/                           signing scripts, versioning, packaging notes
docs/                            this design, ibkr-app-notes.md (spike results)
.github/workflows/               ci.yml, release.yml
```

Tooling: analyzers + `dotnet format`, `MinVer` (tag-driven versions),
`Serilog` → `%LOCALAPPDATA%\IbkrManager\logs\` (rolling 7 days).

---

## 9. GitHub Actions

**`ci.yml`** — on `push` / `pull_request`

- `windows-latest`, `actions/setup-dotnet@v4` (.NET 8), NuGet cache.
- `dotnet restore → build -warnaserror → test --collect:"XPlat Code Coverage" → format --verify-no-changes`.
- Upload test results + coverage artifact; fail on analyzer warnings.
- Optional `codeql.yml` weekly (C#) and `dependabot` for NuGet/Actions.

**`release.yml`** — on tag `v*` (workflow_dispatch with dry-run option)

1. Build + test (reuse `ci` via `workflow_call`).
2. `dotnet publish src/IbkrManager.App -c Release -r win-x64 --self-contained
   -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded`.
3. Sign (optional, off until a cert exists): `signtool sign /fd SHA256` with cert from secrets;
   if absent, publish unsigned and say so in the release notes (SmartScreen warning expected).
4. Package: `IbkrDesktopManager-<version>-win-x64.zip` + `SHA256SUMS` (portable zip only, no installer).
5. `gh release create` with generated notes and all assets; attest build provenance via
   `actions/attest-build-provenance`.

No secrets are needed for CI; signing secrets are the only ones and live in GitHub Environments
(`release`) with required reviewers.

---

## 10. Milestones

| # | Deliverable | Done when |
|---|---|---|
| 0 | **Spike** (`docs/ibkr-app-notes.md`): install real app, inventory install paths, process tree, window classes/titles, accessibility dump of the login form | We know which login driver is realistic | done — automated by the `IBKR Desktop probe` workflow |
| 1 | Skeleton: solution, Avalonia window, `Core` abstractions, CI green | `dotnet test` passes on `windows-latest` | done |
| 2 | Read-only status: locator, process/window polling, credential resolution + source, version | GUI shows live state, no actions yet | done |
| 3 | Accounts dialog + persistence (Credential Manager per account, delete, choose default) | Values survive restart, selection survives restart, delete wipes the secret | done |
| 4 | Lifecycle: start / quit / restart, Job Object, single instance, logs | Start+quit reliable across 20 cycles, no orphans | done (real-app soak still part of the spike) |
| 5 | Auto-login button: validation, click-and-type driver, 2FA hand-off | Clicking Auto Login types the credentials, waits for IBKR's verdict; missing creds produce a clear error; failures fall back to manual | done, verified by the real-app end-to-end test |
| 6 | Release pipeline + settings + signing decision | Tagged release has zip + checksums, CI badge green | done (signing wired, off until a cert exists) |

**Risks**: UI automation breaks on app updates (mitigated by profile config + fail-soft to manual);
2FA blocks unattended start (accepted, surfaced in UI); SmartScreen without signing;
IBKR ToS/account-lockout concerns if auto-login retries (hence no retries).

**Decisions (resolved)**

| Question | Decision |
|---|---|
| Auto-login or launch-only? | Both: plain `Start`, plus a user-clicked `Auto Login` that hard-fails with an error when env vars are missing. Never automatic on launch. |
| Where do credentials live? | Read chain env → user env → Credential Manager → session-only. Setup dialog persists the password in Credential Manager (DPAPI); writing env vars is opt-in because env is plaintext and inherited. |
| Multi-account support? | Single account in v1. Technically possible later only if IBKR Desktop tolerates isolated data dirs per account (unverified); each account would be a separate app data dir + profile, still one app instance at a time. |
| Installer? | Portable zip only (extract & run), signed later if a cert appears. |
| Tray-first? | No — window-first; no tray icon in v0.1.0; closing the window exits. |

---

## 11. Implementation status (v0.4.0)

What shipped, and where it lives:

| Design element | Implementation |
|---|---|
| State machine + orchestrator | `Core/AppManager.cs` (`TickAsync` driven, no background thread) |
| Accounts + redaction | `Core/Accounts.cs` (`AccountService`), `ICredentialStore`, `Secret`, `SecretRedactor`, `RedactingLog` |
| Profile-driven window matching | `Core/AppProfile.cs` + `Core/app-profile.json` |
| Install discovery | `Platform.Windows/WindowsAppLocator.cs` + `UninstallRegistry.cs` |
| Process lifecycle + job object | `Platform.Windows/Win32ProcessController.cs`, `JobObject.cs` |
| Per-account Credential Manager storage | `Platform.Windows/WindowsCredentialStore.cs` (`IbkrDesktopManager/{id}/{kind}`, legacy read for migration) |
| Login drivers | `Platform.Windows/PointerLoginDriver.cs` (`pointer`: click + type, verified) → `UiaLoginDriver.cs` (`uia`: value assignment) → `SendInputLoginDriver.cs` (`sendinput`: blind typing) → `Core/ManualLoginDriver.cs`, chained by `Core/LoginDriverChain.cs` |
| Login-form / 2FA classification (during Auto Login only) | `Core/UiAutomation.cs` (`UiClassifier`) + `Platform.Windows/UiaSession.cs` |
| GUI | `App/Views/MainWindow.axaml`, `AccountsWindow.axaml`, `SettingsWindow.axaml` |
| Tests | 169 xUnit tests, including headless Skia renders of all three windows, a Windows integration suite that drives a stub app through the real platform layer, and a real-app suite run by the probe workflow |
| CI / release | `.github/workflows/ci.yml`, `release.yml` |

Deliberate gaps (also listed in `docs/ibkr-app-notes.md`): tray icon, start-with-Windows,
auto-update, macOS, and exact 2FA/logged-in automation ids (unknown until someone logs in with a
real account). The `ILoginDriver`, `IAppLocator`,
`IProcessController`, `ICredentialStore` and `IPlatformRuntime` seams are what those need.

### What CI can and cannot prove

`windows-latest` runs the integration suite against `src/IbkrManager.TestHost` — a WinForms stand-in
named `ibkr-testhost.exe` registered in `HKCU\...\Uninstall` — so registry discovery, launch/attach,
window-title classification, `WM_CLOSE`, job-object cleanup and process-tree kill are all exercised
against real Win32, not fakes.

Synthetic keyboard input is the exception: hosted Windows runners run jobs in a non-active service
session (`WTSConnectState` ≠ `WTSActive`), so `SendInput` reports success but the keystrokes are
discarded. The keystroke sequence is
unit-tested through the `IKeyboardInput` seam; the delivery test is tagged
`Category=InteractiveDesktop`, detects the session (`OpenInputDesktop`) and skips with a logged
reason elsewhere. CI prints a notice so the gap is visible instead of silently green.
