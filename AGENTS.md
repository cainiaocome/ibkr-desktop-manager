# Project notes for agents

Windows desktop app (C#/.NET 8 + Avalonia 11) that manages the IBKR Desktop client.
Read `docs/design.md` for the architecture and `docs/ibkr-app-notes.md` for the assumptions
that still need a real Windows install to verify.

## Commands

```bash
dotnet build                                    # whole solution (net8.0, warnings are errors)
dotnet test                                     # 159 tests, runs on Linux/Windows/macOS
dotnet format IbkrDesktopManager.sln --verify-no-changes
dotnet run --project src/IbkrManager.App        # needs a display; shows "Windows is required" off-Windows
dotnet publish src/IbkrManager.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/publish
```

Everything except the Win32 calls builds and tests on Linux — the platform layer is guarded with
`OperatingSystem.IsWindows()` and `[SupportedOSPlatform("windows")]`. Windows-only tests self-skip.
CI runs the suite on `windows-latest` and `ubuntu-latest`; trust CI (or a real Windows box) for
anything touching processes, registry or Credential Manager.

`tests/IbkrManager.IntegrationTests` drives a stub app (`src/IbkrManager.TestHost`, WinForms,
`ibkr-testhost.exe`) through the real platform layer on Windows: registry discovery, launch/attach,
window classification, graceful close and process-tree kill.

**Hosted Windows runners run jobs in a non-active service session** (`WTSConnectState` is not
`WTSActive`): windows can be created and focused, but `SendInput` keystrokes are silently discarded. The keystroke *sequence* is unit-tested
with a faked `IKeyboardInput`; actual delivery is covered by
`dotnet test --filter "Category=InteractiveDesktop"`, which only does something on a console/RDP or
self-hosted interactive runner. Do not "fix" a skipped typing test by weakening it — verify the
session with `DesktopSession.IsInteractive()` first.

## Bootstrapping a fresh (Linux) machine

The dev box is ephemeral, so after a reset re-install:

1. **.NET 8 SDK** (nothing is preinstalled):

   ```bash
   curl -sSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
   chmod +x /tmp/dotnet-install.sh
   sudo mkdir -p /usr/local/share/dotnet
   sudo /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/local/share/dotnet --no-path
   sudo ln -sf /usr/local/share/dotnet/dotnet /usr/local/bin/dotnet
   dotnet --version   # 8.0.4xx
   ```

   `global.json` pins SDK 8.0 with `rollForward: latestFeature`, so any 8.0.4xx works.

2. **Optional, only for UI screenshots by hand**: `sudo apt-get install -y xvfb x11-apps imagemagick`
   (`Xvfb :77`, `import -window root out.png`). The committed UI screenshots come from the headless
   Skia tests, so this is not needed for normal work.

3. **Optional, only for validating workflow YAML**: `sudo apt-get install -y python3-yaml`
   (or `python3 -m pip install --break-system-packages pyyaml`), then
   `python3 -c "import yaml,glob; [yaml.safe_load(open(f)) for f in glob.glob('.github/workflows/*.yml')]"`.

4. **Optional, only to lint `scripts/probe-ibkr.ps1` on Linux**: PowerShell 7 tarball into
   `/opt/pwsh` (`curl -sSL -o /tmp/pwsh.tgz https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/powershell-7.4.6-linux-x64.tar.gz`,
   `sudo mkdir -p /opt/pwsh && sudo tar -xzf /tmp/pwsh.tgz -C /opt/pwsh && sudo chmod +x /opt/pwsh/pwsh`), then
   `/opt/pwsh/pwsh -Command '$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path "scripts/probe-ibkr.ps1"),[ref]$t,[ref]$e); $e.Count'`
   (0 = clean). `p7zip-full` is handy for inspecting installers.

NuGet packages restore into `~/.nuget/packages` automatically; no other system dependencies.

## Probing the real app

`gh workflow run ibkr-probe.yml` installs the real IBKR Desktop (install4j, `-q -dir C:\ntws
-overwrite`) on a Windows runner, dumps windows/UIA/screenshot into artifacts, and runs the
`Category=RealIbkDesktop` tests. Locally: `powershell.exe -File scripts/probe-ibkr.ps1`. It needs no
credentials and never submits anything.

Facts learned the hard way (do not regress them):

- The app is Qt 6.8/QML + CEF, not Java/Swing, and its title is always `IBKR Desktop`. Login is
  identified by UIA automation ids (`QApplication.launcherLoginWindow`, button `Log In`), never by
  title.
- `ValuePattern.SetValue` **reports success but IBKR ignores it**: the QML `text` property only
  updates from real input events, so the form stays empty and `Log In` says "enter your username and
  password". Never treat a successful `SetValue` (or a filled-looking field) as a working login. The
  primary driver therefore clicks the field rectangles and types with `SendInput`, and verifies the
  username by reading it back before submitting.
- `Process.MainWindowHandle` is cached by .NET and goes stale when the splash window is replaced —
  use `WindowLocator.FindMainWindow` (EnumWindows, largest visible titled window). A stale handle
  makes UIA fail with `UIA_E_ELEMENTNOTAVAILABLE` (0x80040201).
- Run UIA calls on an STA thread; Qt's provider misbehaves from MTA threads.
- The install4j wizard can stay alive after copying files (it auto-launches the app): poll for
  `ntws.exe` rather than waiting for the installer to exit.
- A real login attempt is asynchronous: "Connecting to server…" replaces the form, and a refusal
  brings it back with a **QML dialog titled "Login failed"** whose text is *not* exposed through UI
  Automation — only its `OK` button is. Detecting the button is the reliable verdict; the wording is
  only visible in screenshots (`TestResults/e2e/e2e-verdict-screen.png`).
- Accessibility calls run inside the app's UI thread: never use `FindAll(Descendants, TrueCondition)`
  and never read four uncached properties per element. The inspector walks breadth-first with a
  depth/node budget, uses a UIA cache request, inspects only login windows deeply, and the manager
  throttles inspections once the app is logged in. Breaking this makes the real app appear frozen
  after login (this already happened once).
- Hosted Windows runners are a service session: `SendInput` is discarded, but posted window messages
  (`WM_LBUTTONDOWN`, `WM_CHAR`) reach the QML fields. Never "fix" a typing test by weakening it —
  check which strategy the driver log says was used.

## Conventions

- Credentials must never reach logs, argv, settings files or the launched app's environment:
  use `Secret`, and log through the `IAppLog` you are given (it is wrapped in `RedactingLog`).
- New IBKR Desktop behaviour (window titles, install paths, timings) belongs in
  `src/IbkrManager.Core/app-profile.json`, not in code.
- Do not commit `artifacts/`, `bin/`, `obj/`, `TestResults/` (ignored).
- Release = push a `v*.*.*` tag; the `Release` workflow builds, tests, packages, signs (when
  `SIGNING_ENABLED` is set) and creates the GitHub release. Keep action versions on Node 24
  (checkout v7, setup-dotnet v6, cache v6, upload-artifact v7, attest-build-provenance v4).
