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
| Setting values | `ValuePattern.SetValue` works on both fields (password reads back masked as `●●●…`) |
| Installer quirk | after copying the files the wizard can keep running (it auto-launches the app); poll for `ntws.exe` instead of waiting for the installer to exit |

Screenshot of the real login window: `docs/images/ibkr-desktop-login.png`. On a GPU-less CI runner
the app also shows "Error: Hardware not supported" — the login form and its accessibility tree work
regardless.

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
3. fails if the observed automation ids no longer match `loginAutomationIdPatterns`;
4. runs the `Category=RealIbkDesktop` tests, which assert that the manager classifies the real login
   screen as `AwaitingLogin` through UIA and that the login controls (with the `Log In` button) are
   discoverable.

That means a new IBKR Desktop release that renames its QML objects turns the workflow red instead of
silently breaking auto-login.
