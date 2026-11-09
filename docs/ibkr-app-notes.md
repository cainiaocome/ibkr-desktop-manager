# IBKR Desktop notes (spike checklist)

The manager's behaviour is driven by `src/IbkrManager.Core/app-profile.json`. This file records what
is known about the real app, what is assumed, and what to verify before trusting auto-login.

## Verified from public sources

| Item | Value | Source |
|---|---|---|
| Windows installer | `ntws-latest-standalone-windows-x64.exe` | IBKR download page |
| Launcher executable | `ntws.exe` | community `ibkr-desktop-linux` project |
| Runtime | Java app (bundled 32-bit Zulu JVM, exe4j-style launcher), `ntws.vmoptions` next to the exe | same |
| Common install directories | `C:\Program Files\IBKR Desktop`, `C:\ntws` | uninstall listings, same project |
| 2FA | Security-code prompt is normal on login | IBKR user guide (Lock and Exit / security tokens) |
| Prior art | IBC automates TWS/Gateway by hooking the Java login frame | IBC project |

## Assumed (not yet verified)

- The main window title contains `Login` / `Log In` / `Sign In` at the login screen.
- The 2FA prompt is a window/dialog whose title contains `Security Code` / `Authentication`.
- After login the main window title contains `IBKR Desktop` or `Portfolio`.
- The login form focuses the username field on open, and `Tab` moves to the password field.
- `Enter` submits the form; there is no separate "I agree / connect" step before 2FA.
- `WM_CLOSE` on the main window starts a clean shutdown, including the Java child processes.

Each assumption maps to `app-profile.json` fields, so a wrong guess is a config edit — not a code
change — except for the tab order and submit behaviour, which the SendInput driver encodes.

## What is already verified, and where

| Check | Verified by |
|---|---|
| Registry discovery, launch, attach, window classification | `windows-latest` integration suite against the stub app |
| Graceful `WM_CLOSE`, kill-on-timeout, process-tree cleanup | same |
| Credential Manager and HKCU\Environment round-trip | platform tests on `windows-latest` |
| Keystroke sequence (select-all, username, Tab, password, Enter) | platform unit tests with a faked keyboard |
| Keystroke *delivery* to a focused control | `Category=InteractiveDesktop` test — needs an active console/RDP/self-hosted session; hosted runners are non-active service sessions and discard injected input |
| Real IBKR window titles, focus-on-open, tab order, Enter-to-submit | **spike below** |

## Spike tasks (do these on a real Windows install)

1. **Locate**: after installing, run `IbkrDesktopManager.exe` and confirm the detected version and
   path. Try a non-default install directory and the Settings override.
2. **Window inventory**: with the login screen and after logging in, run
   `Get-Process ntws,java | Select-Object Id,ProcessName,MainWindowTitle`. Record the exact titles
   into `loginWindowTitlePatterns`, `twoFactorWindowTitlePatterns`, `loggedInWindowTitlePatterns`.
3. **Accessibility surface**: run Accessibility Insights for Windows (or `Inspect.exe`) against the
   login window. If the username/password `Edit` controls are exposed with AutomationIds, a UIA
   driver is worth adding — it removes the tab-order assumption.
4. **Tab order**: open the app, press `Tab` once, type a marker string, and see where it lands.
   Record the correct keystroke sequence.
5. **2FA**: log in with real credentials and capture the 2FA window title, then confirm the manager
   reports *Awaiting 2FA* instead of *Logged in*.
6. **Quit**: press Quit and check Task Manager — no `ntws.exe`/`java.exe` should survive; if the app
   keeps a background updater alive, note it and extend the profile's shutdown timeout.
7. **Elevation / UAC**: confirm no prompt appears when launching from a normal user account. If UAC
   is required, SendInput from a non-elevated process will be blocked by design (UIPI).
8. **Self-update**: let the app update itself and repeat 1–6; log any changed titles.

## Updating the profile

Edit `src/IbkrManager.Core/app-profile.json`, run `dotnet test`, then rebuild. The loader validates
the file (`AppProfile.FromJson`), and `AppProfileTests` pins the default patterns, so a broken edit
fails the build instead of the app.

## Known gaps in v0.1.0

- Only the SendInput driver is implemented (`Native` JVM-property injection and a UIA driver are
  planned, pending the spike above).
- IBKR Desktop is treated as a single instance and a single account.
- No tray icon, no start-with-Windows, no auto-update.
