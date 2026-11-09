using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace IbkrManager.IntegrationTests;

/// <summary>One visible top-level window, used to spot dialogs that the app pops up.</summary>
internal sealed record WindowInfo(int ProcessId, IntPtr Handle, string Title, string ClassName)
{
    public override string ToString() => $"0x{Handle.ToInt64():X} pid={ProcessId} class='{ClassName}' title='{Title}'";
}

/// <summary>
/// Screenshot and window inventory helpers for the end-to-end tests. Everything is best effort: a
/// capture failure must not turn a product failure into an unclear test failure, so callers get null
/// and a warning instead of an exception.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Capture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint PwRenderFullContent = 0x00000002;

    /// <summary>Captures the whole desktop, including any dialog that overlaps the app window.</summary>
    public static bool TryCaptureScreen(string path)
    {
        try
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            bitmap.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Captures a single window, which works even when another window covers it.</summary>
    public static bool TryCaptureWindow(IntPtr window, string path)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(window, hdc, PwRenderFullContent))
                    {
                        return false;
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            bitmap.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Every visible top-level window with a title, ordered by process id.</summary>
    public static IReadOnlyList<WindowInfo> VisibleWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            var title = Title(window);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var pid);
            var className = new StringBuilder(256);
            _ = GetClassName(window, className, className.Capacity);
            windows.Add(new WindowInfo((int)pid, window, title, className.ToString()));
            return true;
        }, IntPtr.Zero);

        return windows.OrderBy(window => window.ProcessId).ToList();
    }

    public static string Title(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowText(window, buffer, buffer.Capacity);
        return copied <= 0 ? string.Empty : buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
}
