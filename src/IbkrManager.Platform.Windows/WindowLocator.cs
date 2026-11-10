using System.Text;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Finds the app's visible top-level window by process id, choosing the largest titled one. Used
/// instead of <see cref="System.Diagnostics.Process.MainWindowHandle"/>, which caches the first
/// window it saw (a splash screen, for example) and never refreshes.
/// </summary>
internal static class WindowLocator
{
    public static IntPtr FindMainWindow(int processId)
    {
        var best = IntPtr.Zero;
        long bestArea = 0;

        foreach (var window in FindWindows(processId))
        {
            if (!NativeMethods.GetWindowRect(window, out var rect))
            {
                continue;
            }

            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                best = window;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>Every visible, titled top-level window of the process, largest first.</summary>
    public static IReadOnlyList<IntPtr> FindWindows(int processId)
    {
        var windows = new List<IntPtr>();

        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId ||
                !NativeMethods.IsWindowVisible(window) ||
                NativeMethods.GetWindowTextLength(window) == 0)
            {
                return true;
            }

            if (NativeMethods.GetWindowRect(window, out var rect))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderByDescending(window =>
            {
                NativeMethods.GetWindowRect(window, out var rect);
                return (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            })
            .ToList();
    }

    public static string? Title(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        var copied = NativeMethods.GetWindowText(window, buffer, buffer.Capacity);
        return copied <= 0 ? null : buffer.ToString();
    }
}
