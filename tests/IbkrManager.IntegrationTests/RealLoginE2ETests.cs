using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// End-to-end login against the real IBKR Desktop: launch it, type a random (invalid) username and
/// password with the production pointer driver, submit, and check that the app rejects the
/// credentials. The "empty fields" answer means the typing never reached the app, which is exactly
/// the bug this test exists to catch.
///
/// Screenshots, the accessibility tree before/after and a JSON summary are written to
/// <c>IBKR_E2E_OUTPUT</c> so the images can be inspected after the run.
/// </summary>
[Collection("windows-ui")]
[Trait("Category", "RealIbkDesktop")]
public sealed class RealLoginE2ETests(ITestOutputHelper output)
{
    private static readonly AppProfile Profile = AppProfile.LoadEmbedded() with
    {
        LoginResultTimeout = TimeSpan.FromSeconds(20),
    };

    private static readonly Regex Rejection = new(
        @"invalid|incorrect|failed|failure|wrong|not\s+(recognised|recognized|valid|found)|denied|unsuccessful|try\s+again",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmptyFormPrompt = new(
        @"(please\s+)?(enter|provide|type)\s+(your\s+)?(username|user\s+name|login).{0,40}password",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool Enabled => Environment.GetEnvironmentVariable("IBKR_REAL_APP") == "1";

    [Fact]
    public async Task Random_credentials_are_typed_and_the_app_rejects_them()
    {
        if (!OperatingSystem.IsWindows() || !Enabled)
        {
            return;
        }

        var username = "e2e_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var password = "Pw!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var outputDirectory = OutputDirectory();
        var log = new RecordingLog();
        var process = new Win32ProcessController(Profile, log);
        var uia = new UiaSession(process, Profile, log);
        var driver = new PointerLoginDriver(
            uia,
            process,
            log,
            isInteractive: InteractiveSession.IsInteractive,
            inspector: uia);
        var manager = new AppManager(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            process,
            new CredentialResolver([new SessionCredentialStore()]),
            new ManualLoginDriver(),
            Profile,
            SystemClock.Instance,
            log,
            inspector: uia);

        var report = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["passwordLength"] = password.Length,
            ["interactive"] = InteractiveSession.IsInteractive(),
        };

        try
        {
            await manager.InitializeAsync();
            await manager.StartAsync();
            report["installation"] = manager.State.Installation?.ExecutablePath;

            var controls = await WaitForLoginControlsAsync(uia, TimeSpan.FromMinutes(3));
            output.WriteLine(string.Join(Environment.NewLine, log.Messages));
            Assert.True(controls is not null, "the real login form never appeared; log:\n" + string.Join(Environment.NewLine, log.Messages));

            var before = uia.Observe();
            CaptureState(outputDirectory, "before", process, before, report);

            var result = await driver.LoginAsync(new LoginRequest(new Secret(username), new Secret(password), Profile));
            report["driverResult"] = result.ToString();
            output.WriteLine($"driver result: {result}");

            var usernameAfter = uia.GetFieldLength(controls!.UsernameIndex) is null ? null : uia.GetFieldValue(controls.UsernameIndex);
            var passwordLengthAfter = uia.GetFieldLength(controls.PasswordIndex);
            report["usernameAfter"] = usernameAfter;
            report["passwordLengthAfter"] = passwordLengthAfter;

            var after = uia.Observe();
            CaptureState(outputDirectory, "after", process, after, report);

            var beforeTexts = TextSet(before);
            var newTexts = TextSet(after).Where(text => !beforeTexts.Contains(text)).ToList();
            report["newTexts"] = newTexts;

            WriteReport(outputDirectory, report);
            output.WriteLine(string.Join(Environment.NewLine, log.Messages));
            output.WriteLine("new text after submitting: " + (newTexts.Count == 0 ? "(none)" : string.Join(" / ", newTexts)));
            output.WriteLine($"username field after submit: '{usernameAfter}' ({passwordLengthAfter?.ToString() ?? "-"} password chars)");

            // 1. The typing must have reached the app.
            Assert.False(string.IsNullOrEmpty(usernameAfter), "the username field is empty after typing: simulated input never reached the app");
            Assert.Equal(username, usernameAfter);
            Assert.True(passwordLengthAfter is > 0, "the password field is empty after typing");

            // 2. The app must have answered. With random credentials that answer is a rejection, and
            //    it must not be the "please enter your username and password" validation message.
            var rejection = newTexts.FirstOrDefault(text => Rejection.IsMatch(text) && !EmptyFormPrompt.IsMatch(text));
            Assert.True(
                rejection is not null,
                "the app did not report a login rejection. Observed new text: " +
                (newTexts.Count == 0 ? "(none)" : string.Join(" / ", newTexts)));

            report["rejection"] = rejection;
            WriteReport(outputDirectory, report);
        }
        finally
        {
            await manager.QuitAsync(force: true);
            WriteReport(outputDirectory, report);
        }
    }

    private static async Task<UiLoginControls?> WaitForLoginControlsAsync(UiaSession uia, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var controls = uia.FindLoginControls();
            if (controls is not null)
            {
                return controls;
            }

            await Task.Delay(1000);
        }

        return null;
    }

    private void CaptureState(
        string directory,
        string prefix,
        Win32ProcessController process,
        UiObservation? observation,
        Dictionary<string, object?> report)
    {
        var windows = Capture.VisibleWindows();
        var appWindow = process.MainWindowHandle ?? IntPtr.Zero;

        var screenPath = Path.Combine(directory, $"e2e-{prefix}-screen.png");
        var windowPath = Path.Combine(directory, $"e2e-{prefix}-window.png");
        var screenSaved = Capture.TryCaptureScreen(screenPath);
        var windowSaved = Capture.TryCaptureWindow(appWindow, windowPath);

        report[$"{prefix}.screenshots"] = new[] { screenSaved ? screenPath : null, windowSaved ? windowPath : null };
        report[$"{prefix}.windows"] = windows.Select(window => window.ToString()).ToList();
        report[$"{prefix}.tree"] = observation is null
            ? null
            : observation.Elements
                .Select(element => $"{element.ControlType}|{element.Name}|{element.AutomationId}")
                .ToList();

        if (!screenSaved)
        {
            output.WriteLine($"WARNING: could not capture the screen ({screenPath})");
        }

        if (!windowSaved)
        {
            output.WriteLine($"WARNING: could not capture window 0x{appWindow.ToInt64():X} ({windowPath})");
        }

        File.WriteAllText(
            Path.Combine(directory, $"e2e-{prefix}-uia.json"),
            JsonSerializer.Serialize(report[$"{prefix}.tree"], new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string OutputDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("IBKR_E2E_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, "e2e");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteReport(string directory, Dictionary<string, object?> report) =>
        File.WriteAllText(
            Path.Combine(directory, "e2e-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    private static HashSet<string> TextSet(UiObservation? observation) =>
        observation is null
            ? []
            : observation.Elements
                .Select(element => element.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
