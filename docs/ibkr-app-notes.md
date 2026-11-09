# IBKR Desktop notes (verified against the real app)

The manager's behaviour is driven by `src/IbkrManager.Core/app-profile.json`. Everything below was
observed by installing the real app on a Windows runner — see the `IBKR Desktop probe` workflow and
`scripts/probe-ibkr.ps1`.

## Verified facts

| Item | Value |
|---|---|
| Installer | `ntws-latest-standalone-windows-x64.exe` (install4j, 208 MB) |
| Unattended install | `installer.exe -q -dir C:\ntws -overwrite` |
| Registry entry | `HKLM\...\Uninstall\<id>`, `DisplayName="IBKR Desktop"`, `InstallLocation="C:\ntws"`, `DisplayVersion="latest (4.0d) 20261028 22:04:17"` |
| Launcher | `C:\ntws\ntws.exe`, a 64-bit native launcher that starts the app in-process (no separate `java.exe` child) |
| UI stack | **Qt 6.8 QML** shell (`Qt683QWindowIcon`, `LoginWindow_QMLTYPE_137`) with embedded CEF widgets — *not* Swing |
| Top-level window | exactly one visible window, class `Qt683QWindowIcon`, title **`IBKR Desktop` in every state** (login and logged-in) |
| Login root | UI Automation AutomationId `QApplication.launcherLoginWindow` |
| Login fields | two `ControlType.Edit` elements, AutomationId `QApplication.launcherLoginWindow.TextField_QMLTYPE_74` |
| Login button | `ControlType.Button`, Name **`Log In`** |
| Field order | first Edit = username (has keyboard focus on open), second = password; `CurrentIsPassword` is not reliably set, so the driver falls back to document order |
| Setting values | `ValuePattern.SetValue` *reports* success on both fields, but IBKR ignores it (see above) |
| Posted window messages | `WM_LBUTTONDOWN`/`WM_CHAR` posted to the window do reach the QML fields (verified in a service session where `SendInput` is discarded) |
| Wrong credentials | "Connecting to server…" progress, then a "Login failed" dialog with an `OK` button |
| Field geometry | both edits and the `Log In` button report a clickable screen rectangle |
| Installer quirk | after copying the files the wizard can keep running (it auto-launches the app); poll for `ntws.exe` instead of waiting for the installer to exit |

Screenshot of the real login window: `docs/images/ibkr-desktop-login.png`. On a GPU-less CI runner
the app also shows "Error: Hardware not supported" — the login form and its accessibility tree work
regardless.

## Why the form must be *typed* into (not set through UI Automation)

`ValuePattern.SetValue` returns success and the accessibility value reads back correctly, **but the
app does not notice**: the QML `text` property that the form validates is only updated by real input
events. With values set that way the fields look filled and pressing `Log In` still produces "enter
your username and password" — the form was empty from IBKR's point of view. Setting only the
password and typing the username by hand shows the same error, which pins the failure on `SetValue`.

So the login driver **clicks each field with the mouse and types with real keystrokes**:

1. `FindLoginControls` supplies the screen rectangles of the username/password edits and the `Log In`
   button (UI Automation reports these even in a session that cannot deliver input).
2. `SetCursorPos` + `SendInput` click the username field, `Ctrl+A`, `Backspace`, then type.
3. The username field is read back; a mismatch aborts the login instead of submitting an empty form.
   This is the guard that turns "looks filled" into a hard error.
4. Same for the password field (never read back, to keep the secret out of the manager).
5. Click the `Log In` button (or press Enter when no rectangle is available).

UI Automation is still used for *identification*, which the constant title cannot provide, and
`ValuePattern.SetValue` remains as a fallback driver for builds that do propagate it.

## How a wrong password looks (verified end to end in CI)

The `IBKR Desktop probe` workflow now logs in for real: it types a random username/password, clicks
`Log In`, and waits. The observed sequence is:

1. the login form is replaced by a progress bar — "Connecting to server (trying for another N
   seconds)…";
2. IBKR's backend refuses the random credentials;
3. the form comes back with a **modal dialog titled "Login failed"** ("Passwords are case sensitive.
   If you need to retrieve your username or reset your password, you can do so here…") and an `OK`
   button.

Important for automation: the dialog's **text is not exposed through UI Automation** — only its
buttons (`OK`) are. So the failure verdict is detected by the dialog's button, and the message itself
is verified by looking at the screenshot artifact (`e2e-verdict-screen.png`).

The two screenshots committed next to the login window show exactly what the manager did in CI:

| Image | What it proves |
|---|---|
| `docs/images/e2e-login-filled.png` | the random username `e2e_…` and 15 password characters are in the form at the moment of submission |
| `docs/images/e2e-login-failed.png` | IBKR attempted the login and answered with the "Login failed" dialog, while the username field still holds the typed value |

## Why the app froze after a successful login

Accessibility calls are executed **inside the app's UI thread**. The original inspector did
`FindAll(Descendants, TrueCondition)` on the main window and then read four properties per element,
uncached, so every snapshot cost the app one full tree traversal plus up to 512 cross-process calls.
That is fine for the 7-element login form and ruinous for the logged-in trading window (Qt/QML plus
CEF content). It was made worse by the post-submit wait loop, which took three snapshots every
500 ms for up to a minute.

Three changes removed the cause:

1. the tree is walked breadth-first with a depth (3) and node (150) budget, expanding only containers;
   anything that is not a login window is inspected two levels deep at most;
2. properties come from a UIA cache request, so an element costs one round trip instead of four;
3. inspections are throttled: once the app is classified as logged in the manager re-checks every
   30 s instead of every GUI tick, and a snapshot that takes longer than 750 ms pauses inspections for
   60 s. The login wait reads one snapshot per second and stops as soon as the form is gone.

The classifier also stopped treating the bare `launcherLoginWindow` root id as "login screen" (the
root keeps that id while it hosts the connecting and logged-in views), which was what kept the old
wait loop polling after a successful login.

## Why UI Automation is the primary login driver

The window title never changes, and the app is a QML/CEF shell, so there is no reliable title to
classify and no stable "Edit" HWND to type into. Accessibility provides both:

- classification: a visible `launcherLoginWindow` root or a `Log In` button means **login**;
- injection: `ValuePattern.SetValue` for both fields, then `InvokePattern` on the `Log In` button —
  no focus stealing, no tab order, no keyboard timing, and it works in a non-interactive session
  where `SendInput` is discarded.

`SendInput` remains as a fallback when accessibility exposes nothing, and as the way to press Enter
when the login button cannot be invoked.

## Still unverified (needs a real account)

| Item | Why |
|---|---|
| Behaviour after valid credentials | would need live credentials; the manager stops at the 2FA prompt |
| 2FA window automation ids | unknown, so `twoFactorAutomationIdPatterns` is empty and the manager reports "Awaiting 2FA" because the login form disappeared |
| Logged-in layout automation ids | unknown, so `loggedInAutomationIdPatterns` is empty; absence of the login form is treated as logged-in |
| Whether login survives sleep/autorestart | out of scope for v1 |

When you run this on your own account, capture the 2FA and post-login automation ids (the probe
script dumps whatever is on screen) and add them to `twoFactorAutomationIdPatterns` /
`loggedInAutomationIdPatterns` — that turns the two heuristics above into exact checks.

## What the probe workflow verifies on every run

`IBKR Desktop probe` (manual + weekly) installs the app, launches it, and:

1. asserts the production locator finds `C:\ntws\ntws.exe`;
2. dumps all top-level windows, child windows, the UIA tree, the focused element and the
   `ValuePattern.SetValue` result into a report + screenshot artifact;
3. fails if the observed automation ids no longer match `loginAutomationIdPatterns`, or if either
   login field stops reporting a clickable rectangle (the pointer driver needs them);
4. runs the `Category=RealIbkDesktop` tests, which assert that accessibility exposes the real login
   form (root id, two fields with clickable rectangles, `Log In` button) and that the manager reports
   the process as running without inspecting the window;

That means a new IBKR Desktop release that renames its QML objects turns the workflow red instead of
silently breaking auto-login.
