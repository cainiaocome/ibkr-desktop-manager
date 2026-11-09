# Project notes for agents

Windows desktop app (C#/.NET 8 + Avalonia 11) that manages the IBKR Desktop client.
Read `docs/design.md` for the architecture and `docs/ibkr-app-notes.md` for the assumptions
that still need a real Windows install to verify.

## Commands

```bash
dotnet build                                    # whole solution (net8.0, warnings are errors)
dotnet test                                     # 95 tests, runs on Linux/Windows/macOS
dotnet format IbkrDesktopManager.sln --verify-no-changes
dotnet run --project src/IbkrManager.App        # needs a display; shows "Windows is required" off-Windows
dotnet publish src/IbkrManager.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts/publish
```

Everything except the Win32 calls builds and tests on Linux — the platform layer is guarded with
`OperatingSystem.IsWindows()` and `[SupportedOSPlatform("windows")]`. Windows-only tests self-skip.
CI runs the suite on `windows-latest` and `ubuntu-latest`; trust CI (or a real Windows box) for
anything touching processes, registry, Credential Manager or SendInput.

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
   (`Xvfb :77`, `import -window root out.png`). The committed screenshots come from the headless
   Skia tests, so this is not needed for normal work.

3. **Optional, only for validating workflow YAML**: `sudo apt-get install -y python3-yaml`
   (or `python3 -m pip install --break-system-packages pyyaml`), then
   `python3 -c "import yaml,glob; [yaml.safe_load(open(f)) for f in glob.glob('.github/workflows/*.yml')]"`.

NuGet packages restore into `~/.nuget/packages` automatically; no other system dependencies.

## Conventions

- Credentials must never reach logs, argv, settings files or the launched app's environment:
  use `Secret`, and log through the `IAppLog` you are given (it is wrapped in `RedactingLog`).
- New IBKR Desktop behaviour (window titles, install paths, timings) belongs in
  `src/IbkrManager.Core/app-profile.json`, not in code.
- Do not commit `artifacts/`, `bin/`, `obj/`, `TestResults/` (ignored).
- Release = push a `v*.*.*` tag; the `Release` workflow builds, tests, packages, signs (when
  `SIGNING_ENABLED` is set) and creates the GitHub release. Keep action versions on Node 24
  (checkout v7, setup-dotnet v6, cache v6, upload-artifact v7, attest-build-provenance v4).
