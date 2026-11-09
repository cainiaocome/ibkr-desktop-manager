using Microsoft.Win32;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Per-user environment block (HKCU\Environment). Writes are broadcast with WM_SETTINGCHANGE so
/// newly launched processes pick the values up; already running processes keep their old block,
/// which is exactly why the manager re-resolves credentials instead of trusting its own env.
/// </summary>
public sealed class WindowsUserEnvironment : IUserEnvironment
{
    private const string EnvironmentKey = @"Environment";

    public bool IsSupported => OperatingSystem.IsWindows();

    public string? Read(string name)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public void Write(string name, string value)
    {
        EnsureSupported();

        using (var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true)
                         ?? throw new InvalidOperationException("Could not open HKCU\\Environment"))
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }

        BroadcastEnvironmentChange();
    }

    public void Delete(string name)
    {
        EnsureSupported();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new InvalidOperationException($"Could not remove user environment variable {name}: {ex.Message}", ex);
        }

        BroadcastEnvironmentChange();
    }

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("User environment variables are only supported on Windows");
        }
    }

    private static void BroadcastEnvironmentChange()
    {
        try
        {
            NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST,
                NativeMethods.WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Environment",
                0x0002 /* SMTO_ABORTIFHUNG */,
                1000,
                out _);
        }
        catch (Exception)
        {
            // Best effort only: new processes will still see the value on their next launch.
        }
    }
}
