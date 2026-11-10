using System.Runtime.InteropServices;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Answers the messages Windows sends when a user logs off or the machine shuts down.
///
/// A GUI app does not have to *veto* a shutdown to stall it: Windows asks every top-level window
/// (<c>WM_QUERYENDSESSION</c>) and, when an app does not answer, waits, marks it "not responding" and
/// puts up a screen that asks someone at the console to retry — which on a headless remote machine
/// means the shutdown never finishes and the box is unreachable.
///
/// This hook answers the query immediately (allow), and on <c>WM_ENDSESSION</c> raises
/// <see cref="SessionEnded"/> so the manager can stop its work instead of holding the session open.
/// </summary>
public sealed class ShutdownHook : IDisposable
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const int GwlWndProc = -4;

    private readonly IntPtr _window;
    private readonly IAppLog _log;
    private readonly WndProc _hook;
    private IntPtr _previous;
    private bool _disposed;

    private ShutdownHook(IntPtr window, IAppLog log)
    {
        _window = window;
        _log = log;
        _hook = Dispatch;
    }

    /// <summary>Subclasses the window, or returns null when the platform does not allow it.</summary>
    public static ShutdownHook? TryInstall(IntPtr window, IAppLog log)
    {
        if (!OperatingSystem.IsWindows() || window == IntPtr.Zero)
        {
            return null;
        }

        var hook = new ShutdownHook(window, log);
        try
        {
            hook._previous = SetWindowLongPtr(window, GwlWndProc, Marshal.GetFunctionPointerForDelegate(hook._hook));
            if (hook._previous == IntPtr.Zero)
            {
                log.Warn($"Could not install the shutdown hook (error {Marshal.GetLastWin32Error()})");
                return null;
            }

            return hook;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Raised after the session has ended, i.e. the machine is going down.</summary>
    public event EventHandler? SessionEnded;

    private IntPtr Dispatch(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmQueryEndSession:
                    // Never delay a shutdown: the answer is "yes" before anything else happens.
                    _log.Info("Windows is ending the session (shutdown or logoff); allowing it");
                    return 1;

                case WmEndSession:
                    if (wParam != IntPtr.Zero)
                    {
                        _log.Info("Session is ending; stopping manager work so shutdown is not delayed");
                        SessionEnded?.Invoke(this, EventArgs.Empty);
                    }

                    return 0;
            }
        }
        catch (Exception ex)
        {
            // An exception must never escape into the window procedure.
            try
            {
                _log.Error("Shutdown hook failed", ex);
            }
            catch (Exception)
            {
                // nothing sensible left to do
            }
        }

        return CallWindowProc(_previous, window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_previous != IntPtr.Zero)
            {
                SetWindowLongPtr(_window, GwlWndProc, _previous);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The process is going away anyway.
        }
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
