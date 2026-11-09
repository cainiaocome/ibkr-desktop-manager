using Microsoft.Win32;

namespace IbkrManager.Platform.Windows;

public sealed record UninstallEntry(string DisplayName, string? DisplayVersion, string? InstallLocation, string? DisplayIcon)
{
    /// <summary>
    /// Directory of the registered icon. Handles the <c>"path\app.exe",0</c> form and both
    /// separators so the value can be asserted on any OS.
    /// </summary>
    public string? IconDirectory
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisplayIcon))
            {
                return null;
            }

            var value = DisplayIcon.Trim();
            if (value.StartsWith('"'))
            {
                var closing = value.IndexOf('"', 1);
                if (closing > 0)
                {
                    value = value[1..closing];
                }
            }
            else
            {
                var comma = value.IndexOf(',');
                if (comma > 0)
                {
                    value = value[..comma];
                }
            }

            var separator = value.LastIndexOfAny(['\\', '/']);
            return separator > 0 ? value[..separator] : null;
        }
    }
}

public interface IUninstallRegistry
{
    IEnumerable<UninstallEntry> ReadEntries();
}

/// <summary>Reads the Windows "Apps &amp; features" entries where IBKR Desktop registers itself.</summary>
public sealed class UninstallRegistry : IUninstallRegistry
{
    private static readonly (RegistryHive Hive, RegistryView View)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.CurrentUser, RegistryView.Registry32),
    ];

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IEnumerable<UninstallEntry> ReadEntries()
    {
        foreach (var (hive, view) in Locations)
        {
            foreach (var entry in ReadFrom(hive, view))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<UninstallEntry> ReadFrom(RegistryHive hive, RegistryView view)
    {
        RegistryKey? root = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            root = baseKey.OpenSubKey(UninstallKey);
            if (root is null)
            {
                yield break;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                RegistryKey? sub = null;
                UninstallEntry? entry = null;
                try
                {
                    sub = root.OpenSubKey(name);
                    var displayName = sub?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    entry = new UninstallEntry(
                        displayName,
                        sub!.GetValue("DisplayVersion") as string,
                        sub.GetValue("InstallLocation") as string,
                        sub.GetValue("DisplayIcon") as string);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    continue;
                }
                finally
                {
                    sub?.Dispose();
                }

                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            root?.Dispose();
        }
    }
}
