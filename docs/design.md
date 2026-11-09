# IBKR Desktop Manager — Design

A small Windows GUI utility that starts, monitors and stops the **IBKR Desktop** app,
and optionally auto-fills the login form with credentials from environment variables,
Windows Credential Manager, or an in-app setup dialog.

---

## 1. Scope

**In scope**

- Detect whether IBKR Desktop is installed / running / which version, with PID + uptime.
- Report whether `IBKR_USERNAME` / `IBKR_PASSWORD` are available, and from which source
  (never display the values).
- Start / graceful quit / force kill / restart the app.
- **Auto Login button**: user-triggered — launches the app if needed and fills the login form;
  refuses with a visible error when no credentials are available.
- **Credentials setup dialog**: enter/update/clear credentials in-app, persisted securely (§7.1).
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
| UIA exposes `QApplication.launcherLoginWindow` with two `TextField_QMLTYPE_74` edits and a `Log In` button, and `ValuePattern.SetValue` works on both fields | UIA is the primary login driver: deterministic, no focus/tab-order, works without an input desktop |
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

### State machine

```
Stopped ──Start──▶ Launching ──window up──▶ AwaitingLogin ──typed──▶ Awaiting2FA
   ▲                    │                        │                      │
   │                    └──── error ──▶ Failed ◀─┴──── error ───────────┤
   └── Quit/exit ◀── Stopping ◀── Quit ── LoggedIn ◀── heuristic ────────┘
```

Each state carries data for the UI: PID, start time, detected version, last error,
"2FA required — finish in the app window". Transitions are driven by a 2 s poll of
`IProcessController` (process tree + window titles), never by sleeping in the UI thread.

Two entry paths, same states: **Start** stops at `AwaitingLogin` and leaves the human to log in;
**Auto Login** continues past `AwaitingLogin` into credential injection and ends at `Awaiting2FA`
(or falls back to `AwaitingLogin` + an error banner if injection fails). Nothing is ever
injected without that explicit click.

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

**Auto Login** (only on button click) — validation first:

- **Auto Login** validation uses the resolved credential (not just the env vars): missing → abort
  before launching, log which field is missing, show an inline error banner. Never prompt, never
  guess.
- Both present → ensure the app is running (launch if `Stopped`), wait for the login window if needed.
- Then run the injection ladder, first success wins, each step switchable in settings:
  1. `Native` — JVM property / config-file injection, *if* the spike proves IBKR Desktop accepts it.
  2. `Uia` — find the two edits via FlaUI, `ValuePattern` or focus+type; clipboard paste
     (restored immediately) for fields that reject typed input.
  3. `SendInput` — focus window, `Tab`/typed keystrokes from the profile.
  4. `Manual` — skip injection, tell the user to type it themselves (fallback, not default).
5. **Stop at 2FA**: never attempt to solve it. Show "Awaiting 2FA" and keep monitoring.
6. **Quit**: post `WM_CLOSE` to the main window → wait 15 s → `Kill(entireProcessTree: true)`.
   Log which path was taken.

Failure policy: any injection step fails → log the reason, show an error banner, drop back to
`AwaitingLogin` so the user can finish manually. Never retry-loop credentials (lockout risk).

---

## 6. GUI

Single 480×360 window, shown on launch (window-first, no start-minimized mode).

```
IBKR Desktop Manager                                   [–][□][×]
──────────────────────────────────────────────────────────────
  IBKR Desktop      ● Running        PID 8420 · 00:12:31
  Version           0.12.0 (installed: 0.11.2)
  Credentials       Username ✓   Password ✓   (env)   [ Set up… ]
  Login state       ● Awaiting 2FA — finish in the app window
──────────────────────────────────────────────────────────────
  [ Start ]  [ Auto Login ]  [ Quit ]  [ Restart ]   [ Logs ] [ ⚙ ]
──────────────────────────────────────────────────────────────
  ⚠ Auto Login blocked: IBKR_PASSWORD is not set
  last: autologin typed username, waiting for password field
```

- Buttons: `Start` (launch, stop at login screen), `Auto Login` (launch if needed + inject),
  `Quit`, `Restart`. `Auto Login` stays clickable so a missing credential produces an explicit
  inline error rather than a mystery-disabled button; a hint sits next to it when a var is absent.
- `[ Set up… ]` on the credentials row opens the dialog in §7.1 (add / replace / clear / test presence).
- Rows use green/amber/red dots; credentials row shows presence + source (`env`), never
  masked values (a mask still leaks length).
- `Start`/`Auto Login` disabled unless the app is installed; `Quit`/`Restart` disabled when stopped.
- Window-first; there is no tray icon in v0.1.0 (the window is the app), and closing the window
  exits the app — no close-to-tray surprise.
- Settings: exe path override, env var names, login driver, timeouts, start-with-Windows.
- Toasts only for terminal states (`Failed`, `Awaiting2FA`).

---

## 7. Security & privacy

- Secrets: `Secret` wrapper type whose `ToString()`/`DebuggerDisplay` is `***`; `ILog` sinks are
  filtered to never receive it. No secrets in argv, logs, temp files, crash dumps, telemetry.
- Zero out unmanaged buffers where practical; keep secrets out of `appsettings.json`.
- The password is never placed in the launched app's environment — injection is UI-only.
- No network calls in v1 except an optional read-only version check (off by default).
- README must state clearly: this automates a UI, it is not sanctioned by IBKR, 2FA is never
  bypassed, and credentials are your responsibility. MIT-licensed, no warranty.

### 7.1 Credential sources & the setup dialog

Read chain, first hit wins, effective source shown in the UI:

| # | Source | At rest | Purpose |
|---|---|---|---|
| 1 | Process env `IBKR_USERNAME` / `IBKR_PASSWORD` | inherited only | CI/ops contract; deliberately wins so automation can override |
| 2 | User env vars (`HKCU\Environment`) | **plaintext** in registry | opt-in only, for tooling that expects env vars |
| 3 | Credential Manager, generic `IbkrDesktopManager/IBKR_*` | DPAPI, per-user | default "Remember" target |
| 4 | Typed into the dialog, this session only | memory | one-off use |

Password defaults to Credential Manager, not an env var — an env password is plaintext in the
registry, readable by any process running as that user, inherited by the launched app and its
children, and easy to leak into logs/dumps. A username is not secret, so env is fine for it.

Setup is an **in-app Avalonia modal, not a system window** (Windows has no per-user env-var
dialog anyway — only `setx` or System Properties → Environment Variables):

```
Credentials
  Username  [ alice                     ]
  Password  [ ••••••••••••  (stored) 👁 ]   only overwritten when edited
  ☑ Remember password (Windows Credential Manager)
  ☐ Also write as user environment variables   ⚠ plaintext in HKCU\Environment
  Effective source: env (IBKR_PASSWORD) — shadows the stored value
  [ Clear ]                                  [ Cancel ] [ Save ]
```

- Writes to user scope only (never machine scope, which needs admin); after saving, refresh the
  in-memory provider and broadcast `WM_SETTINGCHANGE` so newly started processes see the change
  (already-running processes keep their old env — hence the manager re-resolves instead of
  trusting its own inherited env).
- Shadowing is surfaced, never silent: if an env var overrides a stored value, say so.
- `Clear` removes the stored credential *and* any env vars the manager wrote; values from
  external env/1Password are shown as read-only and left alone.
- No prefill of the stored password, reveal toggle instead of clipboard, no "remember me" without
  an explicit checkbox, and the dialog is the only place a credential is ever rendered.

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
| 0 | **Spike** (`docs/ibkr-app-notes.md`): install real app, inventory install paths, process tree, window classes/titles, Accessibility Insights/Inspect dump of the login form, try `-D`/config-arg credential paths | We know which login driver is realistic | **pending** (needs a Windows box) |
| 1 | Skeleton: solution, Avalonia window, `Core` abstractions, CI green | `dotnet test` passes on `windows-latest` | done |
| 2 | Read-only status: locator, process/window polling, credential resolution + source, version | GUI shows live state, no actions yet | done |
| 3 | Credentials dialog + persistence (Credential Manager, opt-in env vars, Clear) | Values survive restart, shadowing is visible, `Clear` wipes both | done |
| 4 | Lifecycle: start / quit / restart, Job Object, single instance, logs | Start+quit reliable across 20 cycles, no orphans | done (real-app soak still part of the spike) |
| 5 | Auto-login button: validation, SendInput driver, 2FA hand-off | Clicking Auto Login reaches the 2FA prompt; missing creds produce a clear error; failures fall back to manual | done (UIA/native drivers pending spike) |
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

## 11. Implementation status (v0.1.2)

What shipped, and where it lives:

| Design element | Implementation |
|---|---|
| State machine + orchestrator | `Core/AppManager.cs` (`TickAsync` driven, no background thread) |
| Credential chain + redaction | `Core/CredentialResolver.cs`, `Secret`, `SecretRedactor`, `RedactingLog` |
| Profile-driven window matching | `Core/AppProfile.cs` + `Core/app-profile.json` |
| Install discovery | `Platform.Windows/WindowsAppLocator.cs` + `UninstallRegistry.cs` |
| Process lifecycle + job object | `Platform.Windows/Win32ProcessController.cs`, `JobObject.cs` |
| Credential Manager + env export | `Platform.Windows/WindowsCredentialStore.cs`, `WindowsUserEnvironment.cs` |
| Login drivers | `Platform.Windows/PointerLoginDriver.cs` (`pointer`: click + type, verified) → `UiaLoginDriver.cs` (`uia`: value assignment) → `SendInputLoginDriver.cs` (`sendinput`: blind typing) → `Core/ManualLoginDriver.cs`, chained by `Core/LoginDriverChain.cs` |
| Login-state classification | `Core/UiAutomation.cs` (`UiClassifier`, accessibility first, title second) + `Platform.Windows/UiaSession.cs` |
| GUI | `App/Views/MainWindow.axaml`, `CredentialsWindow.axaml`, `SettingsWindow.axaml` |
| Tests | 143 xUnit tests, including headless Skia renders of all three windows, a Windows integration suite that drives a stub app through the real platform layer, and a real-app suite run by the probe workflow |
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
