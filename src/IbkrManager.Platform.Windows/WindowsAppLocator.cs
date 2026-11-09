using System.Diagnostics;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Locates <c>ntws.exe</c> via the registry uninstall entries, then the known fallback
/// directories from the profile. The executable path override from settings always wins.
/// </summary>
public sealed class WindowsAppLocator(
    AppProfile profile,
    IUninstallRegistry registry,
    Func<string, bool>? fileExists = null) : IAppLocator
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    public AppInstallation? Locate()
    {
        foreach (var entry in registry.ReadEntries())
        {
            if (!profile.MatchesRegistryName(entry.DisplayName))
            {
                continue;
            }

            foreach (var directory in new[] { entry.InstallLocation, entry.IconDirectory })
            {
                var installation = TryFromDirectory(directory, entry.DisplayVersion);
                if (installation is not null)
                {
                    return installation;
                }
            }
        }

        foreach (var directory in profile.FallbackInstallPaths)
        {
            var installation = TryFromDirectory(directory, null);
            if (installation is not null)
            {
                return installation;
            }
        }

        return null;
    }

    private AppInstallation? TryFromDirectory(string? directory, string? version)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var executable = Path.Combine(directory.Trim().TrimEnd('\\', '/'), profile.ExecutableName);
        return _fileExists(executable) ? FromExecutable(executable, version) : null;
    }

    private static AppInstallation FromExecutable(string executable, string? registryVersion)
    {
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        var version = registryVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            try
            {
                version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
            }
            catch (Exception)
            {
                version = null;
            }
        }

        return new AppInstallation(executable, directory, version);
    }
}

/// <summary>Applies the settings-provided executable override without rebuilding the locator.</summary>
public sealed class OverridingAppLocator(IAppLocator inner, Func<string?> overridePath, IAppLog? log = null) : IAppLocator
{
    private readonly IAppLog _log = log ?? NullAppLog.Instance;

    public AppInstallation? Locate() => ResolveOverride() ?? inner.Locate();

    private AppInstallation? ResolveOverride()
    {
        var path = overridePath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _log.Warn($"Configured executable path does not exist: {path}");
            }

            return null;
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return new AppInstallation(path, Path.GetDirectoryName(path) ?? string.Empty, version);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read version from {path}: {ex.Message}");
            return new AppInstallation(path, Path.GetDirectoryName(path) ?? string.Empty, null);
        }
    }
}
