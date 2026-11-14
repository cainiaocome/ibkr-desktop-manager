using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// End-to-end login against the real IBKR Desktop: launch it, type a random (invalid) username and
/// password with the production pointer driver, submit, and wait for the app to reject the
/// credentials. The "please enter your username and password" answer means the typing never reached
/// the app, which is exactly the bug this test exists to catch.
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
        // IBKR answers asynchronously: the login form is replaced by "Connecting to server…".
        LoginResultTimeout = TimeSpan.FromSeconds(100),
    };

    private static readonly Regex Rejection = new(
        @"invalid|incorrect|failed|failure|wrong|not\s+(recognised|recognized|valid|found)|denied|unsuccessful|try\s+again|could\s+not",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] DialogButtons = ["ok", "okay", "close", "cancel", "retry", "dismiss"];

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
        KillStaleApp();

        var report = new Dictionary<string, object?>();
        var log = new RecordingLog();
        UiLoginControls? controls = null;
        var process = new Win32ProcessController(Profile, log);
        var uia = new UiaSession(process, Profile, log);
        var driver = new PointerLoginDriver(
            uia,
            process,
            log,
            postedInput: new PostedWindowInput(process),
            isInteractive: InteractiveSession.IsInteractive,
            inspector: uia,
            beforeSubmit: async () =>
            {
                // Proof that both fields held the typed values at the moment of submission.
                report["submittedUsername"] = uia.GetFieldValue(controls!.UsernameIndex);
                report["submittedPasswordLength"] = uia.GetFieldLength(controls.PasswordIndex);
                var path = Path.Combine(outputDirectory, "e2e-filled-screen.png");
                if (Capture.TryCaptureScreen(path))
                {
                    report["filledScreenshot"] = path;
                }

                await Task.CompletedTask;
            });
        var manager = new AppManager(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            process,
            new AccountService(new InMemorySettingsStore(), new FakeCredentialStore(), new SessionCredentialStore()),
            new ManualLoginDriver(),
            Profile,
            SystemClock.Instance,
            log);

        report["username"] = username;
        report["passwordLength"] = password.Length;
        report["interactive"] = InteractiveSession.IsInteractive();

        try
        {
            await manager.InitializeAsync();
            await manager.StartAsync();
            report["installation"] = manager.State.Installation?.ExecutablePath;

            controls = await WaitForLoginControlsAsync(manager, uia, TimeSpan.FromMinutes(3));
            output.WriteLine(string.Join(Environment.NewLine, log.Messages));
            Assert.True(
                controls is not null,
                "the real login form never appeared; log:\n" + string.Join(Environment.NewLine, log.Messages));

            var appWindow = process.MainWindowHandle ?? IntPtr.Zero;
            Assert.True(
                Capture.IsResponsive(appWindow),
                "the app stopped answering window messages as soon as the manager started inspecting it");

            var before = uia.Observe();
            CaptureState(outputDirectory, "before", process, before, report);

            var result = await driver.LoginAsync(new LoginRequest(new Secret(username), new Secret(password), Profile));
            report["driverResult"] = result.ToString();
            report["log"] = log.Messages.ToList();
            output.WriteLine($"driver result: {result}");

            var observed = new List<string>();
            var snapshots = new List<string>();
            var verdict = await WaitForVerdictAsync(manager, outputDirectory, observed, snapshots, report);

            report["observedTexts"] = observed;
            report["snapshots"] = snapshots;
            report["verdict"] = verdict is null ? null : new { verdict.Value.Text, verdict.Value.DialogButton };
            WriteReport(outputDirectory, report);

            output.WriteLine(string.Join(Environment.NewLine, log.Messages));
            output.WriteLine("texts after submitting: " + (observed.Count == 0 ? "(none)" : string.Join(" / ", observed)));
            output.WriteLine("verdict: " + (verdict?.ToString() ?? "(none)"));

            // 1. The typing must have reached the app. The driver only submits after verifying that
            //    the username was typed and that the password field reports characters.
            Assert.True(
                log.Messages.Any(message => message.Contains("credentials typed with the", StringComparison.OrdinalIgnoreCase)),
                "the driver never confirmed that the credentials reached the form; log:\n" +
                string.Join(Environment.NewLine, log.Messages));

            // 2. With random credentials the app must reject the login, not complain about empty fields.
            //    IBKR draws its refusal in a QML dialog whose text UI Automation does not expose, so a
            //    dialog button (OK/Close/…) is accepted as the verdict too.
            Assert.True(
                verdict is not null,
                "the app did not report a login rejection. Observed text: " +
                (observed.Count == 0 ? "(none)" : string.Join(" / ", observed)));

            // The driver must not report success when the app answered with a refusal dialog.
            Assert.False(
                result.Success && verdict?.DialogButton is not null,
                $"the driver reported '{result.Message}' although IBKR showed a refusal dialog");
        }
        finally
        {
            await manager.QuitAsync(force: true);
            WriteReport(outputDirectory, report);
        }
    }

    /// <summary>How the app answered the login attempt.</summary>
    private readonly record struct Verdict(string? Text, string? DialogButton)
    {
        public override string ToString() =>
            Text is not null ? $"text: \"{Text}\"" : $"dialog button: \"{DialogButton}\"";
    }

    /// <summary>
    /// Polls every window of the app until it answers the login attempt, capturing screenshots along
    /// the way so the sequence can be inspected after the run.
    /// </summary>
    private async Task<Verdict?> WaitForVerdictAsync(
        AppManager manager,
        string outputDirectory,
        List<string> observed,
        List<string> snapshots,
        Dictionary<string, object?> report)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(150);
        var nextCapture = DateTimeOffset.UtcNow;
        var index = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var appWindows = Capture.VisibleWindows()
                .Where(window => manager.State.ProcessId is { } pid && window.ProcessId == pid)
                .ToList();
            var texts = appWindows
                .SelectMany(window => UiaDump.TextsOf(window.Handle).Concat([window.Title]))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var text in texts.Where(text => !observed.Contains(text, StringComparer.OrdinalIgnoreCase)))
            {
                observed.Add(text);
            }

            var verdict = new Verdict(
                texts.FirstOrDefault(text => Rejection.IsMatch(text) && !EmptyFormPrompt.IsMatch(text)),
                texts.FirstOrDefault(text => DialogButtons.Contains(text, StringComparer.OrdinalIgnoreCase)));
            if (verdict.Text is not null || verdict.DialogButton is not null)
            {
                var path = Path.Combine(outputDirectory, "e2e-verdict-screen.png");
                if (Capture.TryCaptureScreen(path))
                {
                    snapshots.Add(path);
                }

                report["verdictWindows"] = appWindows.Select(window => window.ToString()).ToList();
                report["verdictTexts"] = texts;
                return verdict;
            }

            if (DateTimeOffset.UtcNow >= nextCapture)
            {
                var path = Path.Combine(outputDirectory, $"e2e-watch-{index:00}-screen.png");
                if (Capture.TryCaptureScreen(path))
                {
                    snapshots.Add(path);
                }

                report[$"watch.{index:00}"] = texts;
                index++;
                nextCapture = DateTimeOffset.UtcNow.AddSeconds(15);
            }

            await Task.Delay(1000);
        }

        return null;
    }

    /// <summary>Advances the state machine and waits for the QML tree to expose the login form.</summary>
    private static async Task<UiLoginControls?> WaitForLoginControlsAsync(AppManager manager, UiaSession uia, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await manager.TickAsync();
            var controls = uia.FindLoginControls();
            if (controls is not null)
            {
                return controls;
            }

            await Task.Delay(1000);
        }

        return null;
    }

    /// <summary>A leftover IBKR Desktop from an earlier test would make this test attach to it.</summary>
    private static void KillStaleApp()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("ntws"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to kill.
            }
            finally
            {
                process.Dispose();
            }
        }
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
    }

    private static string OutputDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("IBKR_E2E_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, "e2e");
        }
        else if (!Path.IsPathRooted(directory))
        {
            // The workflow passes a workspace-relative path, but tests run from the build output.
            var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            directory = string.IsNullOrWhiteSpace(root)
                ? Path.GetFullPath(directory)
                : Path.GetFullPath(Path.Combine(root, directory));
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteReport(string directory, Dictionary<string, object?> report) =>
        File.WriteAllText(
            Path.Combine(directory, "e2e-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
