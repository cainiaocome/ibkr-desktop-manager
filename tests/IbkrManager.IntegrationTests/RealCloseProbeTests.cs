using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// Measures what the real IBKR Desktop does when its window is asked to close: does it exit, does it
/// ask for confirmation, and which window/button carries that question? The answer decides whether a
/// graceful Quit can honour the app's cleanup or has to fall back to terminating the tree.
///
/// Writes <c>close-probe.json</c> plus one screenshot per snapshot to <c>IBKR_E2E_OUTPUT</c>.
/// </summary>
[Collection("windows-ui")]
[Trait("Category", "RealIbkDesktop")]
public sealed class RealCloseProbeTests(ITestOutputHelper output)
{
    private static readonly AppProfile Profile = AppProfile.LoadEmbedded();

    private static bool Enabled => Environment.GetEnvironmentVariable("IBKR_REAL_APP") == "1";

    [Fact]
    public async Task Closing_the_window_shows_what_the_app_does_before_it_exits()
    {
        if (!OperatingSystem.IsWindows() || !Enabled)
        {
            return;
        }

        var directory = OutputDirectory();
        KillStaleApp();

        var log = new RecordingLog();
        var report = new Dictionary<string, object?>();
        var process = new Win32ProcessController(Profile, log);
        var uia = new UiaSession(process, Profile, log);
        var manager = new AppManager(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            process,
            new AccountService(new InMemorySettingsStore(), new FakeCredentialStore(), new SessionCredentialStore()),
            new ManualLoginDriver(),
            Profile,
            SystemClock.Instance,
            log);
        var snapshots = new List<object>();

        try
        {
            await manager.InitializeAsync();
            await manager.StartAsync();

            var loginWindow = await WaitForLoginControlsAsync(manager, uia, TimeSpan.FromMinutes(3));
            Assert.True(
                loginWindow is not null,
                "the login form never appeared; log:\n" + string.Join(Environment.NewLine, log.Messages));

            var alive = process.MainWindowHandle ?? IntPtr.Zero;
            Assert.True(Capture.IsResponsive(alive), "the app was not answering messages before the close request");
            snapshots.Add(Snapshot(directory, snapshots.Count, "before", process, manager));

            var requestedAt = DateTimeOffset.UtcNow;
            var requested = await process.CloseMainWindowAsync();
            report["closeRequested"] = requested;
            output.WriteLine($"WM_CLOSE posted: {requested}");

            var exitedAfter = (int?)null;
            for (var i = 1; i <= 14; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
                var running = process.IsRunning;
                if (!running && exitedAfter is null)
                {
                    exitedAfter = (int)(DateTimeOffset.UtcNow - requestedAt).TotalMilliseconds;
                }

                snapshots.Add(Snapshot(directory, snapshots.Count, $"{i:00}", process, manager));
                output.WriteLine(
                    $"t+{i * 1.5:0.0}s: running={running} windows=" +
                    string.Join(", ", Capture.VisibleWindows()
                        .Where(window => window.ProcessId == manager.State.ProcessId)
                        .Select(window => $"{window.ClassName}#{window.Title}")));

                if (!running && i >= 3)
                {
                    break;
                }
            }

            report["exitedAfterMilliseconds"] = exitedAfter;
            report["survivedTheClose"] = process.IsRunning;
            output.WriteLine(exitedAfter is null
                ? "the app did not exit after WM_CLOSE"
                : $"the app exited after {exitedAfter} ms");

            // The production Quit path, against the same real app: it must report a graceful close and
            // must not need to terminate anything.
            var relaunched = await manager.StartAsync();
            report["quit.relaunched"] = relaunched.Status.ToString();
            Assert.True(
                await WaitForLoginControlsAsync(manager, uia, TimeSpan.FromMinutes(3)) is not null,
                "the login form did not come back after the relaunch");

            var quitStarted = DateTimeOffset.UtcNow;
            var quit = await manager.QuitAsync();
            report["quit.status"] = quit.Status.ToString();
            report["quit.lastAction"] = quit.LastAction;
            report["quit.lastError"] = quit.LastError;
            report["quit.elapsedMilliseconds"] = (int)(DateTimeOffset.UtcNow - quitStarted).TotalMilliseconds;
            report["quit.stillRunning"] = process.IsRunning;
            report["log"] = log.Messages.ToList();
            output.WriteLine($"manager.QuitAsync() -> {quit.Status}: {quit.LastAction}");

            Assert.False(process.IsRunning, "the app was still running after Quit");
            Assert.Equal("Stopped", quit.Status.ToString());
            Assert.Equal("Closed gracefully", quit.LastAction);
        }
        finally
        {
            report["snapshots"] = snapshots;
            WriteReport(directory, report);
            try
            {
                await manager.QuitAsync(force: true);
            }
            catch (Exception ex)
            {
                output.WriteLine("cleanup failed: " + ex.Message);
            }
        }
    }

    private object Snapshot(string directory, int index, string label, Win32ProcessController process, AppManager manager)
    {
        var pid = manager.State.ProcessId;
        var windows = Capture.VisibleWindows().Where(window => window.ProcessId == pid).ToList();
        var path = Path.Combine(directory, $"close-probe-{index:00}-{label}.png");
        var captured = Capture.TryCaptureScreen(path);

        return new
        {
            index,
            label,
            atMilliseconds = (int?)null,
            processAlive = process.IsRunning,
            screenshot = captured ? path : null,
            windows = windows.Select(window => new
            {
                handle = $"0x{window.Handle.ToInt64():X}",
                title = window.Title,
                @class = window.ClassName,
                responsive = Capture.IsResponsive(window.Handle),
                elements = UiaDump.Elements(window.Handle),
                texts = UiaDump.TextsOf(window.Handle),
            }).ToList(),
        };
    }

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
                // already gone
            }
            finally
            {
                process.Dispose();
            }
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
            var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            directory = string.IsNullOrWhiteSpace(root)
                ? Path.GetFullPath(directory)
                : Path.GetFullPath(Path.Combine(root, directory));
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteReport(string directory, Dictionary<string, object?> report)
    {
        File.WriteAllText(
            Path.Combine(directory, "close-probe.json"),
            System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
