using System.Runtime.InteropServices;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Whether this process can reach the *input* desktop. Windows can be created, focused and closed in
/// a service session (GitHub-hosted runners, scheduled tasks in session 0), but <c>SendInput</c> is
/// silently discarded because there is no input desktop — so the pointer login driver must not claim
/// it typed anything there.
/// </summary>
public static class InteractiveSession
{
    private const uint DesktopReadObjects = 0x0001;
    private const int WtsCurrentSession = -1;
    private const int WtsConnectState = 8;
    private const int WtsActive = 1;

    public static bool IsInteractive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // A disconnected RDP session or a service session reports WTSDisconnected/WTSIdle and
        // silently drops injected input, so the connection state is the reliable signal.
        if (!IsSessionActive())
        {
            return false;
        }

        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        CloseDesktop(desktop);
        return true;
    }

    public static string Describe() =>
        $"session={System.Diagnostics.Process.GetCurrentProcess().SessionId}, " +
        $"interactive={IsInteractive()}, user={Environment.UserName}";

    private static bool IsSessionActive()
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsConnectState, out buffer, out var bytes) || bytes < 4)
            {
                return false;
            }

            return Marshal.ReadInt32(buffer) == WtsActive;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
