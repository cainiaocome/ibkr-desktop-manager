using System.Diagnostics;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Windows process control for the IBKR Desktop launcher: finds an already running instance,
/// launches under a kill-on-close job object, focuses the main window, asks it to close, and
/// falls back to terminating the whole process tree.
/// </summary>
public sealed class Win32ProcessController : IProcessController, IDisposable
{
    private readonly IAppLog _log;
    private readonly Func<string, Process[]> _findProcesses;
    private readonly string _processName;
    private Process? _owned;
    private JobObject? _job;
    private DateTimeOffset? _startedAt;
    private bool _disposed;

    public Win32ProcessController(AppProfile profile, IAppLog log, Func<string, Process[]>? findProcesses = null)
    {
        _log = log;
        _findProcesses = findProcesses ?? Process.GetProcessesByName;
        _processName = Path.GetFileNameWithoutExtension(profile.ExecutableName);
    }

    public bool IsRunning => Find() is not null;

    public int? ProcessId => Read(static process => (int?)process.Id, null);

    public DateTimeOffset? StartedAt => Read(
        process =>
        {
            try
            {
                return (DateTimeOffset?)new DateTimeOffset(process.StartTime);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return _startedAt;
            }
        },
        _startedAt);

    public string? MainWindowTitle => Read(
        static process => WindowLocator.Title(WindowLocator.FindMainWindow(process.Id)),
        null);

    public bool HasVisibleWindow => Read(static process => WindowLocator.FindMainWindow(process.Id) != IntPtr.Zero, false);

    /// <summary>
    /// Live lookup instead of <see cref="Process.MainWindowHandle"/>: that property is cached by
    /// .NET and goes stale as soon as the app swaps its splash window for the real one, which
    /// then makes UI Automation fail with UIA_E_ELEMENTNOTAVAILABLE.
    /// </summary>
    public IntPtr? MainWindowHandle => Read(
        static process => WindowLocator.FindMainWindow(process.Id) is var handle && handle != IntPtr.Zero
            ? (IntPtr?)handle
            : null,
        null);

    public Task StartAsync(AppInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            WorkingDirectory = installation.InstallDirectory,
            UseShellExecute = false,
        };

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Could not start {installation.ExecutablePath}");

        _owned = process;
        _startedAt = DateTimeOffset.Now;
        _log.Info($"Started {installation.FileName} (pid {process.Id})");

        try
        {
            _job = JobObject.CreateKillOnClose();
            if (_job.TryAssign(process))
            {
                _log.Info("Assigned IBKR Desktop to a kill-on-close job object");
            }
            else
            {
                _log.Warn($"Could not assign the job object ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Job object unavailable: {ex.Message}. The app may outlive the manager.");
        }

        return Task.CompletedTask;
    }

    public bool TryFocusMainWindow()
    {
        var process = Find();
        if (process is null)
        {
            return false;
        }

        IntPtr handle;
        try
        {
            handle = WindowLocator.FindMainWindow(process.Id);
        }
        finally
        {
            Release(process);
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        var foregroundBefore = NativeMethods.GetForegroundWindow();
        if (NativeMethods.SetForegroundWindow(handle))
        {
            _log.Info($"Focused window 0x{handle.ToInt64():X} (foreground was 0x{foregroundBefore.ToInt64():X})");
            return true;
        }

        // SetForegroundWindow is refused when the calling thread does not own the foreground
        // window; attaching input briefly is the standard workaround.
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return false;
        }

        if (!NativeMethods.AttachThreadInput(currentThread, foregroundThread, true))
        {
            return false;
        }

        try
        {
            var focused = NativeMethods.SetForegroundWindow(handle);
            _log.Info(
                focused
                    ? $"Focused window 0x{handle.ToInt64():X} via AttachThreadInput (foreground was 0x{foregroundBefore.ToInt64():X})"
                    : $"SetForegroundWindow failed for 0x{handle.ToInt64():X} (foreground 0x{foregroundBefore.ToInt64():X}, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return focused;
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public IReadOnlyList<IntPtr> FindWindows()
    {
        var process = Find();
        if (process is null)
        {
            return [];
        }

        try
        {
            return WindowLocator.FindVisibleWindows(process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
        finally
        {
            Release(process);
        }
    }

    public Task<bool> CloseMainWindowAsync(CancellationToken cancellationToken = default)
    {
        var process = Find();
        if (process is null)
        {
            return Task.FromResult(true);
        }

        try
        {
            var handle = WindowLocator.FindMainWindow(process.Id);
            if (handle == IntPtr.Zero)
            {
                // No window to close (e.g. still starting up) but the process exists.
                _log.Warn("No main window to close; will wait for exit or terminate");
                return Task.FromResult(true);
            }

            var requested = NativeMethods.PostMessage(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!requested)
            {
                _log.Warn($"PostMessage(WM_CLOSE) failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }

            return Task.FromResult(requested);
        }
        finally
        {
            Release(process);
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (!IsRunning)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    public Task KillAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_owned is { } owned)
        {
            try
            {
                owned.Kill(entireProcessTree: true);
                _log.Warn($"Terminated pid {owned.Id} and its children");
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn($"Could not terminate pid {owned.Id}: {ex.Message}");
            }
        }

        foreach (var process in _findProcesses(_processName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                _log.Warn($"Terminated pid {process.Id} and its children");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn($"Could not terminate pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    private Process? Find()
    {
        if (_owned is { } owned)
        {
            try
            {
                if (!owned.HasExited)
                {
                    return owned;
                }
            }
            catch (InvalidOperationException)
            {
                // Process was never started or is already gone.
            }
        }

        Process[] candidates;
        try
        {
            candidates = _findProcesses(_processName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        Process? best = null;
        DateTime bestStart = DateTime.MinValue;
        foreach (var candidate in candidates)
        {
            DateTime start;
            try
            {
                start = candidate.StartTime;
            }
            catch (Exception)
            {
                start = DateTime.MinValue;
            }

            if (best is null || start >= bestStart)
            {
                if (best is not null && !ReferenceEquals(best, _owned))
                {
                    best.Dispose();
                }

                best = candidate;
                bestStart = start;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return best;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the job handle terminates the app if the manager goes away.
        _job?.Dispose();
        _job = null;

        _owned?.Dispose();
        _owned = null;
    }

    private T Read<T>(Func<Process, T> read, T fallback)
    {
        var process = Find();
        if (process is null)
        {
            return fallback;
        }

        try
        {
            return read(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return fallback;
        }
        finally
        {
            Release(process);
        }
    }

    private void Release(Process process)
    {
        if (!ReferenceEquals(process, _owned))
        {
            process.Dispose();
        }
    }
}
