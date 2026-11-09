namespace IbkrManager.Platform.Windows;

/// <summary>Posts mouse and keyboard messages to the app's main window.</summary>
public sealed class PostedWindowInput(IProcessController process) : IPostedInput
{
    public bool Click(int screenX, int screenY)
    {
        if (process.MainWindowHandle is not { } window || window == IntPtr.Zero)
        {
            return false;
        }

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(window, ref point))
        {
            return false;
        }

        var position = MakeLParam(point.X, point.Y);
        NativeMethods.PostMessage(window, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, position);
        NativeMethods.PostMessage(window, NativeMethods.WM_LBUTTONDOWN, NativeMethods.MK_LBUTTON, position);
        NativeMethods.PostMessage(window, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, position);
        return true;
    }

    public bool Text(char character)
    {
        if (process.MainWindowHandle is not { } window || window == IntPtr.Zero)
        {
            return false;
        }

        // WM_CHAR carries the UTF-16 code unit; lParam 1 means "one keystroke".
        NativeMethods.PostMessage(window, NativeMethods.WM_CHAR, character, 1);
        return true;
    }

    public bool KeyPress(ushort virtualKey)
    {
        if (process.MainWindowHandle is not { } window || window == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.PostMessage(window, NativeMethods.WM_KEYDOWN, virtualKey, 1);
        NativeMethods.PostMessage(window, NativeMethods.WM_KEYUP, virtualKey, unchecked((nint)(1 | (1 << 30) | (1 << 31))));
        return true;
    }

    private static IntPtr MakeLParam(int low, int high) => (high << 16) | (low & 0xFFFF);
}
