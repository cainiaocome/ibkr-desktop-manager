# Using IBKR Desktop Manager

## First run

1. Extract the zip and start `IbkrDesktopManager.exe` (no installer, no admin rights).
2. The status card tells you whether IBKR Desktop was found. If it says *Not installed*, either
   install IBKR Desktop or point the manager at `ntws.exe` via **Settings → executable override**.
3. Press **Start**. The manager launches the app and waits for its login window.
4. Log in as usual, or configure credentials (§2) and press **Auto Login**.

![Main window](images/main-window.png)

The window refreshes every 2 seconds (process state only, which is cheap) and never blocks while a
command runs. **It does not track what the app is showing**: the manager reports whether the process is
running and what your last Auto Login did, nothing more. That keeps it out of the app's way — reading
another program's window on every tick costs that program time.

## 1. States

| Status | Meaning |
|---|---|
| Not installed | `ntws.exe` was not found in the registry or the known folders |
| Not running | Installed, process not running |
| Running | The process is running — whether you are at the login screen, at a 2FA prompt or inside the trading app is up to you to see in the app |
| Stopping… / Error | Shutdown in progress, or the last command failed (see the red banner) |

The **Login** row shows the outcome of the last Auto Login click (submitted, awaiting 2FA, or failed),
with the time it happened. It is a record of an action, not a live status.

## 2. Credentials

Open **Credentials**. The dialog shows where the current values come from and never pre-fills the
stored password.

![Credentials dialog](images/credentials-window.png)

- **Remember password** (default on) stores both values in Windows Credential Manager, encrypted with
  DPAPI for your user account.
- **Also write as user environment variables** (default off) additionally writes
  `IBKR_USERNAME` / `IBKR_PASSWORD` to `HKCU\Environment`. This is plaintext in the registry and is
  inherited by every process you start, so only enable it when some other tool insists on env vars.
- **Clear stored** removes the Credential Manager entries and any environment values the manager wrote.

Environment variables still win over stored values. That is intentional: a shell that sets them can
override the stored credential without touching the dialog. If that happens the dialog says so
("Currently using … env").

### Variable names

Defaults are `IBKR_USERNAME` and `IBKR_PASSWORD`; rename them in **Settings** if they clash with
something else.

## 3. Auto Login — what it really does

Pressing **Auto Login**:

1. refuses to run if either value is missing, with an inline error such as
   `Auto Login blocked: IBKR_PASSWORD is not set` — nothing is launched in that case;
2. launches IBKR Desktop if it is not already running and waits for the login window;
3. **clicks the fields and types the credentials like a person** — UI Automation supplies the screen
   rectangles, the manager clicks the username field, replaces its contents and types, moves to the
   password with `Tab` (falling back to a click), types it, then clicks `Log In`;
4. **verifies the typing landed** before submitting: the username is read back, the password field
   must report characters, and the username must still be intact (which proves the password went
   into the password field rather than being appended to the username);
5. **waits for IBKR to answer**, within that one click. The login form is replaced by "Connecting to
   server…", so a missing form is not treated as success: the manager watches (one snapshot per second,
   only while the click runs) and reports *IBKR reported a login failure* (quoting the app's message
   when it exposes one), *Awaiting 2FA*, or a timeout. When the click finishes, so does the watching —
   the window keeps a note of the outcome, and nothing polls the app afterwards.

If real mouse/keyboard events are discarded — a service session, a scheduled task, or IBKR Desktop
running elevated — the manager repeats the same clicks and keystrokes as posted window messages,
which the app processes in its own event loop. You will see `credentials typed with the posted
strategy` in the log when that happens.

It is UI automation, not an IBKR API. Consequences:

- IBKR Desktop is a Qt/QML app with a constant window title (`IBKR Desktop`), so the manager detects
  the login screen by its QML automation ids (`QApplication.launcherLoginWindow`) rather than by the
  title. If a future release renames those objects, auto-login degrades to `manual` and the
  `IBKR Desktop probe` workflow turns red (see [ibkr-app-notes.md](ibkr-app-notes.md)).
- It needs an **interactive desktop**: the manager checks first, and declines with a clear message in
  a service session or on a disconnected RDP session where Windows discards synthetic input.
- Don't click into another window while it runs — keystrokes go to the focused control.
- If IBKR Desktop runs **elevated** (e.g. launched by an installer as administrator), Windows blocks
  input from a non-elevated manager. The manager then reports that the app did not receive the typed
  username; restart both at the same privilege level.
- It types into whatever has focus after the manager brings the window forward — do not click into
  another window while it runs.
- It never solves 2FA and never retries credentials (retrying is how accounts get locked).
- If it fails, it says so and leaves the app at the login screen; nothing is left half-typed that a
  manual login cannot fix (the username field is re-selected each attempt).

Set the login driver to `manual` to make Auto Login report "disabled in Settings" and do nothing else.

## 4. Quit, restart and closing the manager

- **Quit** asks the app to close (`WM_CLOSE`) and waits. If IBKR Desktop puts up its own confirmation —
  it does so on a running session, not at the login screen — the manager reads the dialog, presses its
  confirming button and waits again, so the app gets to log out and flush its state. Quit **never
  terminates the process** on its own. If the app stays open, the manager says so and leaves it alone.
- **Force Quit** terminates the process tree immediately. Use it when the app will not close, or when
  you do not care about its cleanup. Nothing else in the manager kills the app.
- **Restart** = Quit + Start. Because Quit never kills, a Restart is refused (with an explanation)
  while the app would not close; Force Quit and then Start instead.
- Closing the manager window stops the IBKR Desktop instance the manager started. This is deliberate:
  the app is assigned to a kill-on-close job object so a trading session cannot be left orphaned.
  If you want the app to outlive the manager, start it yourself — the manager will then attach to it
  read-only and leave it alone.

## 5. Settings

![Settings dialog](images/settings-window.png)

| Setting | Default | Notes |
|---|---|---|
| Executable override | empty | full path to `ntws.exe`; wins over registry discovery |
| Username / password variable | `IBKR_USERNAME` / `IBKR_PASSWORD` | must differ |
| Login driver | `auto` | or `manual` (older settings files that say `sendinput` load as `auto`) |

Settings live in `%LOCALAPPDATA%\IbkrManager\settings.json` and never contain secrets.

## 6. Troubleshooting

| Symptom | Fix |
|---|---|
| "IBKR Desktop is not installed" | Install it, or set the executable override in Settings |
| `Auto Login blocked: … is not set` | Add the value in Credentials, or set the env var and restart the manager |
| Auto Login reports "login window is still showing" | Wrong credentials, extra dialog on screen, or the app changed its login form → try `manual` |
| "the app did not receive the typed username" | Another window took focus, a UAC prompt is in front, or the app is elevated while the manager is not. Click into the IBKR window and retry, or run both at the same privilege level |
| Status stays "Starting…" | The app may be showing a splash window; check the app window and `%LOCALAPPDATA%\IbkrManager\logs` |
| The machine did not shut down and is unreachable | If the manager was running an older build (≤ 0.1.4) its window could be busy inspecting the app when Windows asked it to close, which parks the shutdown on a screen that needs console input. 0.2.1 answers session-end immediately and never blocks its message loop. For headless machines use `shutdown /s /f /t 0` so unresponsive apps are terminated instead of waited for |
| IBKR Desktop becomes unresponsive after logging in | Fixed in 0.1.4: the manager no longer traverses the logged-in window's accessibility tree every couple of seconds. Update, or run the app without the manager to confirm the app itself is healthy |
| App shows "Error: Hardware not supported" | IBKR Desktop needs a supported GPU; the manager still starts/quits it, and UIA may still work |
| Auto Login types into the wrong field | Tab order changed; use `manual` and report it with the log |
| SmartScreen warning | Expected for unsigned binaries; verify the SHA-256 from `SHA256SUMS` |
| Nothing in the tray | v0.1.0 has no tray icon; the window is the app |
| Manager closed and the trading app disappeared | Kill-on-close job object; see §4 |

Logs: `%LOCALAPPDATA%\IbkrManager\logs\manager-YYYY-MM-DD.log`, kept for 7 days, secrets redacted.

## 7. Uninstall

Delete the extracted folder, then optionally `%LOCALAPPDATA%\IbkrManager` and the stored credential
(**Credentials → Clear stored**).
