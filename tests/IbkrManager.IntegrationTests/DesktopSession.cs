using System.Runtime.InteropServices;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// Tells whether this process can reach the *input* desktop. GitHub-hosted Windows runners execute
/// jobs in a service session: windows can be created, focused and closed, but <c>SendInput</c> is
/// silently dropped because there is no input desktop. Tests that inject keystrokes therefore only
/// run on an interactive session (console, RDP or a self-hosted runner).
/// </summary>
internal static class DesktopSession
{
    private const uint DesktopReadObjects = 0x0001;

    public static bool IsInteractive()
    {
        if (!OperatingSystem.IsWindows())
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
