using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

/// <summary>
/// Windows paths are compared through <see cref="Normalize"/> so these tests are meaningful on
/// Linux/macOS too (Path.Combine uses the host separator).
/// </summary>
public sealed class WindowsAppLocatorTests
{
    private static readonly AppProfile Profile = AppProfile.Default with
    {
        ExecutableName = "ntws.exe",
        RegistryDisplayNamePatterns = ["IBKR Desktop"],
        FallbackInstallPaths = [@"C:\Program Files\IBKR Desktop", @"C:\ntws"],
    };

    [Fact]
    public void Locate_prefers_the_registry_install_location()
    {
        var registry = new FakeRegistry(new UninstallEntry("IBKR Desktop 0.12", "0.12.0", @"D:\IBKR", null));
        var locator = new WindowsAppLocator(Profile, registry, Matches(@"D:\IBKR\ntws.exe"));

        var installation = locator.Locate();

        Assert.NotNull(installation);
        Assert.Equal(Normalize(@"D:\IBKR\ntws.exe"), Normalize(installation!.ExecutablePath));
        Assert.Equal("0.12.0", installation.Version);
    }

    [Fact]
    public void Locate_falls_back_to_the_display_icon_directory()
    {
        var registry = new FakeRegistry(new UninstallEntry("IBKR Desktop", null, null, @"""D:\Apps\IBKR\ntws.exe"",0"));
        var locator = new WindowsAppLocator(Profile, registry, Matches(@"D:\Apps\IBKR\ntws.exe"));

        var installation = locator.Locate();

        Assert.Equal(Normalize(@"D:\Apps\IBKR\ntws.exe"), Normalize(installation!.ExecutablePath));
    }

    [Fact]
    public void Locate_falls_back_to_known_directories()
    {
        var registry = new FakeRegistry();
        var locator = new WindowsAppLocator(Profile, registry, Matches(@"C:\ntws\ntws.exe"));

        var installation = locator.Locate();

        Assert.NotNull(installation);
        Assert.Equal(Normalize(@"C:\ntws\ntws.exe"), Normalize(installation!.ExecutablePath));
    }

    [Fact]
    public void Locate_ignores_unrelated_registry_entries()
    {
        var registry = new FakeRegistry(
            new UninstallEntry("Trader Workstation", "10.30", @"C:\TWS", null),
            new UninstallEntry("IBKR Desktop", "0.12.0", @"D:\IBKR", null));
        var locator = new WindowsAppLocator(Profile, registry, Matches(@"D:\IBKR\ntws.exe"));

        Assert.Equal(Normalize(@"D:\IBKR\ntws.exe"), Normalize(locator.Locate()!.ExecutablePath));
    }

    [Fact]
    public void Locate_returns_null_when_nothing_exists()
    {
        var locator = new WindowsAppLocator(Profile, new FakeRegistry(), _ => false);
        Assert.Null(locator.Locate());
    }

    [Fact]
    public void Override_wins_over_discovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ibkr-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "ntws.exe");
        File.WriteAllText(executable, string.Empty);

        try
        {
            var inner = new WindowsAppLocator(Profile, new FakeRegistry(), _ => true);
            var locator = new OverridingAppLocator(inner, () => executable);

            var installation = locator.Locate();

            Assert.Equal(Path.GetFullPath(executable), Path.GetFullPath(installation!.ExecutablePath));
            Assert.Equal(directory, installation.InstallDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Override_is_ignored_when_the_file_is_missing()
    {
        var inner = new WindowsAppLocator(Profile, new FakeRegistry(), _ => false);
        var locator = new OverridingAppLocator(inner, () => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ntws.exe"));

        Assert.Null(locator.Locate());
    }

    [Fact]
    public void UninstallEntry_icon_directory_is_unquoted()
    {
        var entry = new UninstallEntry("IBKR Desktop", null, null, @"""C:\Program Files\IBKR\ntws.exe"",0");
        Assert.Equal(Normalize(@"C:\Program Files\IBKR"), Normalize(entry.IconDirectory!));
    }

    private static Func<string, bool> Matches(string expected) =>
        path => string.Equals(Normalize(path), Normalize(expected), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed class FakeRegistry(params UninstallEntry[] entries) : IUninstallRegistry
    {
        public IEnumerable<UninstallEntry> ReadEntries() => entries;
    }
}
