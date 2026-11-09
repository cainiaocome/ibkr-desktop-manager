<#
.SYNOPSIS
    Installs the real IBKR Desktop on a Windows machine, launches it, and records everything the
    manager depends on: install path, window titles/classes, UI Automation tree, and whether the
    credential fields can be set through UIA.

.DESCRIPTION
    Runs on Windows PowerShell 5.1 (UIAutomationClient is .NET Framework only) and on GitHub's
    windows-latest runners. It never logs in: only the login screen is inspected, and any value it
    writes into the fields is a harmless probe string that is not submitted.

    Outputs (into -OutputDirectory): report.md, windows.json, uia.json, screenshot.png, installer.log.

.PARAMETER InstallerUrl
    Override the download URL. Defaults to IBKR's "latest standalone" Windows build.

.PARAMETER InstallDirectory
    Where to install. Defaults to C:\ntws (one of the paths the manager probes).

.PARAMETER SkipInstall
    Use an existing installation (still discovered through the manager's own rules).
#>
[CmdletBinding()]
param(
    [string]$InstallerUrl = 'https://download2.interactivebrokers.com/installers/ntws/latest-standalone/ntws-latest-standalone-windows-x64.exe',
    [string]$InstallDirectory = 'C:\ntws',
    [string]$OutputDirectory = 'artifacts/ibkr-probe',
    [switch]$SkipInstall,
    [int]$LaunchTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Section([string]$title) {
    Write-Host ''
    Write-Host "== $title" -ForegroundColor Cyan
}

function Get-InstallerPath {
    param([string]$Url)
    $dir = Join-Path $env:LOCALAPPDATA 'ibkr-installer'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir 'ntws-latest-standalone-windows-x64.exe'

    if (Test-Path $file) {
        Write-Host "Cached installer found: $file"
        # -z only downloads when the remote copy is newer.
        & curl.exe -sSL -R -z $file -o $file $Url
        if ($LASTEXITCODE -ne 0) { throw "curl exit code $LASTEXITCODE while refreshing the installer" }
    } else {
        Write-Host "Downloading $Url"
        & curl.exe -sSL -o $file $Url
        if ($LASTEXITCODE -ne 0) { throw "curl exit code $LASTEXITCODE while downloading the installer" }
    }

    return $file
}

function Invoke-Installer {
    param([string]$Installer, [string]$Target, [string]$LogPath)

    $attempts = @(
        @{ Name = 'install4j unattended'; Args = @('-q', '-dir', $Target, '-overwrite') },
        @{ Name = 'NSIS silent';         Args = @('/S', "/D=$Target") },
        @{ Name = 'install4j console';   Args = @('-c', '-dir', $Target, '-overwrite') }
    )

    foreach ($attempt in $attempts) {
        Write-Host "Trying $($attempt.Name): $Installer $($attempt.Args -join ' ')"
        # Quote arguments that contain spaces (e.g. an install dir under Program Files).
        $quotedArgs = @($attempt.Args | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
        $proc = Start-Process -FilePath $Installer -ArgumentList $quotedArgs -PassThru -Wait -NoNewWindow `
            -RedirectStandardOutput $LogPath -RedirectStandardError "$LogPath.err"
        Write-Host "  exit code: $($proc.ExitCode)"

        $exe = Join-Path $Target 'ntws.exe'
        if (Test-Path $exe) {
            return $true
        }

        Start-Sleep -Seconds 3
    }

    return $false
}

function Find-Installation {
    # Mirror WindowsAppLocator: uninstall registry keys first, then the known directories.
    $patterns = @('IBKR Desktop', 'IBKR-D')
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    $found = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if (-not $props.DisplayName) { continue }
            foreach ($pattern in $patterns) {
                if ($props.DisplayName -like "*$pattern*") {
                    $found += [pscustomobject]@{
                        RegistryKey    = $key.PSPath
                        DisplayName    = $props.DisplayName
                        DisplayVersion = $props.DisplayVersion
                        InstallLocation = $props.InstallLocation
                        DisplayIcon    = $props.DisplayIcon
                    }
                }
            }
        }
    }

    $exe = $null
    foreach ($entry in $found) {
        foreach ($candidate in @($entry.InstallLocation, (Split-Path -Parent ($entry.DisplayIcon -replace '"', '').Split(',')[0]))) {
            if (-not $candidate) { continue }
            $path = Join-Path $candidate 'ntws.exe'
            if (Test-Path $path) { $exe = $path; break }
        }
        if ($exe) { break }
    }

    if (-not $exe) {
        foreach ($dir in @('C:\Program Files\IBKR Desktop', 'C:\Program Files (x86)\IBKR Desktop', 'C:\ntws')) {
            $path = Join-Path $dir 'ntws.exe'
            if (Test-Path $path) { $exe = $path; break }
        }
    }

    return [pscustomobject]@{ RegistryEntries = $found; Executable = $exe }
}

# ---------------------------------------------------------------- Win32 helpers
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class ProbeWindow
{
    public IntPtr Handle;
    public string Title;
    public string ClassName;
    public int ProcessId;
    public bool Visible;
    public int X, Y, Width, Height;
}

public static class ProbeNative
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<ProbeWindow> Enumerate(int[] processIds)
    {
        var wanted = new HashSet<int>(processIds);
        var result = new List<ProbeWindow>();
        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!wanted.Contains((int)pid)) return true;

            var title = new StringBuilder(1024);
            GetWindowText(hWnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            RECT rect;
            GetWindowRect(hWnd, out rect);

            result.Add(new ProbeWindow
            {
                Handle = hWnd,
                Title = title.ToString(),
                ClassName = cls.ToString(),
                ProcessId = (int)pid,
                Visible = IsWindowVisible(hWnd),
                X = rect.Left, Y = rect.Top,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@ | Out-Null

function Get-ProcessTree([int]$RootId) {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath
    $ids = @($RootId)
    $queue = @($RootId)
    while ($queue.Count -gt 0) {
        $current = $queue[0]
        $queue = @($queue | Select-Object -Skip 1)
        foreach ($child in $all | Where-Object { $_.ParentProcessId -eq $current }) {
            if ($ids -notcontains [int]$child.ProcessId) {
                $ids += [int]$child.ProcessId
                $queue += [int]$child.ProcessId
            }
        }
    }

    return [pscustomobject]@{ Ids = $ids; Processes = $all | Where-Object { $ids -contains [int]$_.ProcessId } }
}

function Save-WindowScreenshot([IntPtr]$Handle, [string]$Path) {
    Add-Type -AssemblyName System.Drawing
    try {
        $bitmap = New-Object System.Drawing.Bitmap 1920, 1080
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        $ok = [ProbeNative]::PrintWindow($Handle, $hdc, 2)
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
        if ($ok) {
            $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $bitmap.Dispose()
        return $ok
    } catch {
        Write-Warning "screenshot failed: $_"
        return $false
    }
}

function Get-UiaTree {
    param([IntPtr]$Handle, [int]$MaxDepth = 8)

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    if (-not $root) { return @() }

    $nodes = New-Object System.Collections.ArrayList
    $script:uiaMap = @{}

    function Walk($element, [int]$depth, [string]$path) {
        if ($depth -gt $MaxDepth) { return }

        $current = $element.Current
        $patterns = @()
        $patternMap = @{
            'ValuePattern'             = [System.Windows.Automation.ValuePattern]::Pattern
            'InvokePattern'            = [System.Windows.Automation.InvokePattern]::Pattern
            'SelectionItemPattern'     = [System.Windows.Automation.SelectionItemPattern]::Pattern
            'TextPattern'              = [System.Windows.Automation.TextPattern]::Pattern
            'TogglePattern'            = [System.Windows.Automation.TogglePattern]::Pattern
            'ExpandCollapsePattern'    = [System.Windows.Automation.ExpandCollapsePattern]::Pattern
            'LegacyIAccessiblePattern' = [System.Windows.Automation.LegacyIAccessiblePattern]::Pattern
        }
        foreach ($name in $patternMap.Keys) {
            try {
                $null = $element.GetCurrentPattern($patternMap[$name])
                $patterns += $name
            } catch { }
        }

        $node = [pscustomobject]@{
            Path          = $path
            Depth         = $depth
            Name          = $current.Name
            AutomationId  = $current.AutomationId
            ControlType   = $current.ControlType.ProgrammaticName
            ClassName     = $current.ClassName
            IsEnabled     = $current.IsEnabled
            IsOffscreen   = $current.IsOffscreen
            IsKeyboardFocusable = $current.IsKeyboardFocusable
            HasKeyboardFocus    = $current.HasKeyboardFocus
            BoundingRectangle   = "$([int]$current.BoundingRectangle.X),$([int]$current.BoundingRectangle.Y) $([int]$current.BoundingRectangle.Width)x$([int]$current.BoundingRectangle.Height)"
            Patterns      = ($patterns -join ',')
            Value         = $null
        }

        if ($patterns -contains 'ValuePattern') {
            try {
                $valuePattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $node.Value = $valuePattern.Current.Value
            } catch { }
        }

        [void]$nodes.Add($node)
        $script:uiaMap[$path] = $element

        $children = $element.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $children.Count; $i++) {
            Walk $children.Item($i) ($depth + 1) "$path/$i"
        }
    }

    Walk $root 0 '0'
    return $nodes
}

# ---------------------------------------------------------------- main
if (-not $env:OS -or $env:OS -ne 'Windows_NT') {
    throw 'This probe only runs on Windows'
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$installerLog = Join-Path $OutputDirectory 'installer.log'
$summary = [ordered]@{
    StartedAt    = (Get-Date).ToString('o')
    InstallerUrl = $InstallerUrl
    Installed    = $false
    Installation = $null
    Windows      = @()
    Processes    = @()
    LoginWindow  = $null
    ProfileMatch = $null
}

Write-Section 'Install'
if (-not $SkipInstall) {
    $installer = Get-InstallerPath -Url $InstallerUrl
    Write-Host ("Installer: {0} ({1:N0} MB)" -f $installer, ((Get-Item $installer).Length / 1MB))
    $installed = Invoke-Installer -Installer $installer -Target $InstallDirectory -LogPath $installerLog
    Write-Host "installer reported success: $installed"
} else {
    Write-Host 'Skipping the installer (-SkipInstall)'
}

$discovery = Find-Installation
$summary.Installation = $discovery
$summary.Installed = [bool]$discovery.Executable
Write-Host "Discovered executable: $($discovery.Executable)"
foreach ($entry in $discovery.RegistryEntries) {
    Write-Host ("  registry: {0} | {1} | {2}" -f $entry.DisplayName, $entry.DisplayVersion, $entry.InstallLocation)
}

if (-not $discovery.Executable) {
    $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'report.json')
    throw 'IBKR Desktop was not installed or could not be discovered'
}

Write-Section 'Launch'
$workDir = Split-Path -Parent $discovery.Executable
$process = Start-Process -FilePath $discovery.Executable -WorkingDirectory $workDir -PassThru
Write-Host "Started ntws.exe (pid $($process.Id)) from $workDir"

$deadline = (Get-Date).AddSeconds($LaunchTimeoutSeconds)
$loginWindow = $null
$windows = @()
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if ($process.HasExited) {
        throw "ntws.exe exited during startup with code $($process.ExitCode)"
    }

    $tree = Get-ProcessTree -RootId $process.Id
    $windows = @([ProbeNative]::Enumerate([int[]]$tree.Ids))
    $visible = $windows | Where-Object { $_.Visible -and $_.Title } | Sort-Object { $_.Width * $_.Height } -Descending

    foreach ($window in $visible) {
        if ($window.Title -match 'Login|Log In|Sign In|Logon') {
            $loginWindow = $window
            break
        }
    }

    if ($loginWindow) { break }
    Write-Host ("  waiting... {0} window(s), titles: {1}" -f $windows.Count, (($visible | ForEach-Object { $_.Title }) -join ' | '))
}

$tree = Get-ProcessTree -RootId $process.Id
$summary.Processes = @($tree.Processes | ForEach-Object {
    [pscustomobject]@{ ProcessId = $_.ProcessId; Name = $_.Name; ExecutablePath = $_.ExecutablePath }
})
$summary.Windows = @($windows | ForEach-Object {
    [pscustomobject]@{
        Handle = ('0x{0:X}' -f $_.Handle.ToInt64()); Title = $_.Title; ClassName = $_.ClassName
        ProcessId = $_.ProcessId; Visible = $_.Visible
        Size = "$($_.Width)x$($_.Height)"
    }
})

if (-not $loginWindow) {
    Write-Warning 'No login window appeared before the timeout'
} else {
    Write-Section "Login window: '$($loginWindow.Title)' (class $($loginWindow.ClassName))"
    $summary.LoginWindow = [pscustomobject]@{
        Title = $loginWindow.Title; ClassName = $loginWindow.ClassName
        Handle = ('0x{0:X}' -f $loginWindow.Handle.ToInt64()); ProcessId = $loginWindow.ProcessId
        Size = "$($loginWindow.Width)x$($loginWindow.Height)"
    }

    [void][ProbeNative]::ShowWindow($loginWindow.Handle, 9)
    [void][ProbeNative]::SetForegroundWindow($loginWindow.Handle)
    $null = Save-WindowScreenshot -Handle $loginWindow.Handle -Path (Join-Path $OutputDirectory 'screenshot.png')

    Write-Section 'UI Automation tree'
    try {
        $uia = @(Get-UiaTree -Handle $loginWindow.Handle)
        $uia | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'uia.json')
        $uia | Format-Table Depth, ControlType, Name, AutomationId, ClassName, Patterns, HasKeyboardFocus -AutoSize |
            Out-String -Width 220 | Write-Host

        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($focused) {
            Write-Host ("Focused element: {0} '{1}' (automation id '{2}')" -f $focused.Current.ControlType.ProgrammaticName, $focused.Current.Name, $focused.Current.AutomationId)
        }

        $edits = $uia | Where-Object { $_.ControlType -eq 'ControlType.Edit' }
        Write-Host ("Editable fields: {0}" -f (($edits | ForEach-Object { "$($_.Name)/$($_.AutomationId)" }) -join ', '))

        # Can a UIA ValuePattern set the fields? That would allow a far more robust driver than SendInput.
        $uiaProbe = @()
        $index = 0
        foreach ($edit in $edits) {
            $index++
            $element = $script:uiaMap[$edit.Path]
            if (-not $element) { continue }

            $result = [ordered]@{
                Index = $index; Name = $edit.Name; AutomationId = $edit.AutomationId
                SetValue = $false; ReadBack = $null; Error = $null
            }

            try {
                $valuePattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $valuePattern.SetValue("probe-$index")
                $result.SetValue = $true
                $result.ReadBack = $valuePattern.Current.Value
            } catch {
                $result.Error = $_.Exception.Message
            }

            $uiaProbe += [pscustomobject]$result
        }

        if ($uiaProbe.Count -gt 0) {
            Write-Host 'UIA SetValue probe:'
            $uiaProbe | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
            $summary['UiaSetValue'] = $uiaProbe
        } else {
            $summary['UiaSetValue'] = @()
        }
    } catch {
        Write-Warning "UIA inspection failed: $_"
        $summary['UiaError'] = "$_"
    }
}

# Compare the observed titles with the patterns the manager ships.
Write-Section 'Profile match'
$profile = Get-Content (Join-Path $PSScriptRoot '..\src\IbkrManager.Core\app-profile.json') -Raw | ConvertFrom-Json
$match = [ordered]@{
    LoginPatterns = $profile.loginWindowTitlePatterns
    ObservedLoginTitle = if ($loginWindow) { $loginWindow.Title } else { $null }
    ClassifiedAsLogin = $false
    TitleMatchesPattern = $false
}
if ($loginWindow) {
    foreach ($pattern in $profile.loginWindowTitlePatterns) {
        if ($loginWindow.Title -like "*$pattern*") { $match.TitleMatchesPattern = $true; break }
    }
    $match.ClassifiedAsLogin = $match.TitleMatchesPattern -or ($loginWindow.Title -match 'Login|Log In|Sign In|Logon')
}
Write-Host ($match | ConvertTo-Json -Depth 4)

# Shut the app down so the runner does not keep a JVM around.
if (-not $process.HasExited) {
    $null = $process.CloseMainWindow()
    Start-Sleep -Seconds 5
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'java.exe' -or $_.Name -eq 'ntws.exe' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

$summary.ProfileMatch = $match
$summary.FinishedAt = (Get-Date).ToString('o')
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'report.json')

# Markdown summary for the workflow run page.
$exeText = if ($discovery.Executable) { $discovery.Executable } else { 'NOT FOUND' }
$titleText = if ($loginWindow) { $loginWindow.Title } else { 'NOT FOUND' }
$classText = if ($loginWindow) { $loginWindow.ClassName } else { '-' }
$uiaCount = if ($summary['UiaSetValue']) { $summary['UiaSetValue'].Count } else { 0 }

$markdown = @()
$markdown += '# IBKR Desktop probe'
$markdown += ''
$markdown += '| Item | Value |'
$markdown += '|---|---|'
$markdown += ('| Discovered executable | `' + $exeText + '` |')
$markdown += ('| Login window title | `' + $titleText + '` |')
$markdown += ('| Window class | `' + $classText + '` |')
$markdown += ('| Matches a login pattern | ' + $match.TitleMatchesPattern + ' |')
$markdown += ('| UIA editable fields | ' + $uiaCount + ' |')
$markdown += ''
$markdown += '## Windows'
$markdown += ''
$markdown += '| PID | Class | Title | Visible | Size |'
$markdown += '|---|---|---|---|---|'
foreach ($window in $summary.Windows) {
    $markdown += ('| ' + $window.ProcessId + ' | `' + $window.ClassName + '` | ' + $window.Title + ' | ' + $window.Visible + ' | ' + $window.Size + ' |')
}
$markdown | Set-Content (Join-Path $OutputDirectory 'report.md')

if ($env:GITHUB_STEP_SUMMARY) {
    $markdown | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}

if (-not $loginWindow) {
    exit 2
}

if (-not $match.TitleMatchesPattern) {
    Write-Error "The login window title '$($loginWindow.Title)' does not match app-profile.json ($($profile.loginWindowTitlePatterns -join ', '))"
    exit 3
}

Write-Host 'Probe completed: install discovery and login-window classification verified against the real app' -ForegroundColor Green
exit 0
