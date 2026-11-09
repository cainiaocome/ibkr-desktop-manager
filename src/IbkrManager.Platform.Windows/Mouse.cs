using System.Runtime.InteropServices;

namespace IbkrManager.Platform.Windows;

/// <summary>Physical mouse injection (move + left click).</summary>
internal static class Mouse
{
    /// <summary>Last SendInput/SetCursorPos error code (0 on success).</summary>
    public static int LastError { get; private set; }

    public static bool Click(int x, int y)
    {
        if (!NativeMethods.SetCursorPos(x, y))
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }

        // Let the app see the pointer arrive before the button goes down (QML hover handlers).
        Thread.Sleep(30);

        var down = Button(NativeMethods.MOUSEEVENTF_LEFTDOWN);
        var up = Button(NativeMethods.MOUSEEVENTF_LEFTUP);
        var sent = NativeMethods.SendInput(2, [down, up], Marshal.SizeOf<NativeMethods.INPUT>());
        LastError = sent == 2 ? 0 : Marshal.GetLastWin32Error();
        return sent == 2;
    }

    private static NativeMethods.INPUT Button(uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        mi = new NativeMethods.MOUSEINPUT { dwFlags = flags },
    };
}
