using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// Runs against a real IBKR Desktop installation. Not part of the normal CI matrix (the app is a
/// 208 MB download and needs a JVM), it is enabled by the "IBKR Desktop probe" workflow with
/// IBKR_REAL_APP=1 after the installer has run.
///
/// It checks the assumptions the manager cannot fake: the production locator finds the real
/// install, and UI Automation identifies the Qt/QML login screen even though the window title is
/// the constant "IBKR Desktop".
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
    public async Task Launch_is_classified_as_the_login_screen_through_ui_automation()
    {
        if (!OperatingSystem.IsWindows() || !Enabled)
        {
            return;
        }

        var log = new RecordingLog();
        var process = new Win32ProcessController(Profile, log);
        var uia = new UiaSession(process, Profile, log);
        var manager = new AppManager(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            process,
            new CredentialResolver([new SessionCredentialStore()]),
            new ManualLoginDriver(),
            Profile,
            SystemClock.Instance,
            log,
            inspector: uia);

        try
        {
            await manager.InitializeAsync();
            await manager.StartAsync();

            // The QML tree fills in a few seconds after the window handle exists.
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (DateTimeOffset.UtcNow < deadline && uia.FindLoginControls() is null)
            {
                Thread.Sleep(1000);
            }

            var state = await manager.TickAsync();
            output.WriteLine(string.Join(Environment.NewLine, log.Messages));

            var observation = uia.Observe();
            output.WriteLine($"title: '{state.WindowTitle}' status: {state.Status}");
            if (observation is not null)
            {
                output.WriteLine($"uia ({observation.Elements.Count} elements): {observation.Describe(20)}");
            }

            Assert.NotNull(observation);
            Assert.False(observation!.IsEmpty, "UI Automation should expose the login form");
            Assert.True(
                observation.HasAutomationId(Profile.LoginAutomationIdPatterns),
                $"no element matched loginAutomationIdPatterns [{string.Join(", ", Profile.LoginAutomationIdPatterns)}]; " +
                $"observed: {observation.Describe(20)}");

            // The state machine must use the content signal, not the constant window title.
            Assert.Equal(AppStatus.AwaitingLogin, state.Status);

            var controls = uia.FindLoginControls();
            Assert.NotNull(controls);
            output.WriteLine($"login controls: {controls}");
            Assert.True(controls!.CanSubmit, "the Log In button should be invokable");

            // The pointer login driver clicks these rectangles, so they must be usable even in a
            // session that cannot deliver input (which is exactly what a hosted runner is).
            Assert.False(controls.UsernameBounds.IsEmpty, $"username field has no clickable rectangle: {controls.UsernameBounds}");
            Assert.False(controls.PasswordBounds.IsEmpty, $"password field has no clickable rectangle: {controls.PasswordBounds}");
            Assert.False(controls.SubmitBounds.IsEmpty, $"Log In button has no clickable rectangle: {controls.SubmitBounds}");

            // Reading a field must work too: the driver uses it to prove the keystrokes landed.
            Assert.NotNull(uia.GetFieldValue(controls.UsernameIndex));
        }
        finally
        {
            await manager.QuitAsync();
        }
    }
}
