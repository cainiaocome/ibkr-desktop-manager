using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// Runs against a real IBKR Desktop installation. Not part of the normal CI matrix (the app is a
/// 208 MB download and needs a JVM), it is enabled by the "IBKR Desktop probe" workflow with
/// IBKR_REAL_APP=1 after the installer has run.
///
/// It checks the two assumptions the manager cannot fake: the production locator finds the real
/// install, and the shipped profile classifies the real login window as a login window.
/// </summary>
[Collection("windows-ui")]
[Trait("Category", "RealIbkDesktop")]
public sealed class RealIbkDesktopTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("IBKR_REAL_APP") == "1";

    private static readonly AppProfile Profile = AppProfile.LoadEmbedded();

    [Fact]
    public void Production_locator_and_profile_find_the_real_installation()
    {
        if (!OperatingSystem.IsWindows() || !Enabled)
        {
            return;
        }

        var installation = new WindowsAppLocator(Profile, new UninstallRegistry()).Locate();

        Assert.NotNull(installation);
        output.WriteLine($"executable: {installation!.ExecutablePath}");
        output.WriteLine($"directory : {installation.InstallDirectory}");
        output.WriteLine($"version   : {installation.Version ?? "unknown"}");

        Assert.EndsWith("ntws.exe", installation.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(installation.InstallDirectory), "install directory should exist");
    }

    [Fact]
    public async Task Launch_reaches_a_login_window_that_the_profile_recognises()
    {
        if (!OperatingSystem.IsWindows() || !Enabled)
        {
            return;
        }

        var log = new RecordingLog();
        var locator = new WindowsAppLocator(Profile, new UninstallRegistry());
        var process = new Win32ProcessController(Profile, log);
        var resolver = new CredentialResolver([new SessionCredentialStore()]);
        var manager = new AppManager(
            locator,
            process,
            resolver,
            new ManualLoginDriver(),
            Profile,
            SystemClock.Instance,
            log);

        try
        {
            await manager.InitializeAsync();
            Assert.NotNull(manager.State.Installation);

            var state = await manager.StartAsync();
            output.WriteLine(string.Join(Environment.NewLine, log.Messages));
            output.WriteLine($"title: '{state.WindowTitle}'  status: {state.Status}");

            Assert.False(string.IsNullOrWhiteSpace(state.WindowTitle), "the app should expose a main window title");
            Assert.Equal(WindowKind.Login, Profile.Classify(state.WindowTitle));
        }
        finally
        {
            await manager.QuitAsync();
        }
    }
}
