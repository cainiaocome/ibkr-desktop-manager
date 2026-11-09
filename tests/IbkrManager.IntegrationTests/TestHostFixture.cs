using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// Runs the stub IBKR Desktop app (<c>ibkr-testhost.exe</c>) on a real Windows desktop and wires the
/// production manager to it exactly like the app does: registry discovery, Win32 process control and
/// SendInput keyboard injection.
/// </summary>
public sealed class TestHostFixture : IDisposable
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IbkrDesktopManagerTestHost";
    private const string HostFileName = "ibkr-testhost.exe";

    private readonly List<Process> _launched = [];
    private readonly List<string> _outputFiles = [];

    public TestHostFixture()
    {
        HostDirectory = Path.Combine(AppContext.BaseDirectory, "testhost");
        HostPath = Path.Combine(HostDirectory, HostFileName);
        HostAvailable = File.Exists(HostPath);

        if (!HostAvailable)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey)!;
        key.SetValue("DisplayName", "IBKR Desktop (test host)");
        key.SetValue("InstallLocation", HostDirectory);
        key.SetValue("DisplayVersion", "9.9.9");
    }

    /// <summary>False when the stub was not copied next to the tests (a packaging bug).</summary>
    public bool HostAvailable { get; }

    public string HostDirectory { get; }

    public string HostPath { get; }

    public AppProfile Profile { get; } = AppProfile.Default with
    {
        DisplayName = "IBKR test host",
        ExecutableName = HostFileName,
        RegistryDisplayNamePatterns = ["IBKR Desktop"],
        LoginWindowTitlePatterns = ["- Login"],
        TwoFactorWindowTitlePatterns = ["Security Code"],
        LoggedInWindowTitlePatterns = ["Portfolio"],
        LaunchTimeout = TimeSpan.FromSeconds(30),
        LoginWindowTimeout = TimeSpan.FromSeconds(30),
        LoginResultTimeout = TimeSpan.FromSeconds(30),
        GracefulShutdownTimeout = TimeSpan.FromSeconds(3),
        TypingDelayMilliseconds = 0,
        SubmitAfterPassword = true,
    };

    public Harness CreateHarness(string? username = null, string? password = null, params string[] extraArguments)
    {
        var output = Path.Combine(Path.GetTempPath(), "ibkr-testhost-" + Guid.NewGuid().ToString("N") + ".json");
        _outputFiles.Add(output);

        var log = new RecordingLog();
        var process = new Win32ProcessController(Profile, log);
        var session = new SessionCredentialStore();
        var resolver = new CredentialResolver([session]);
        if (username is not null)
        {
            session.SaveAsync(new Secret(username), Secret.From(password), CredentialWriteOptions.SessionOnly)
                .GetAwaiter().GetResult();
        }

        var driver = new SendInputLoginDriver(process, SystemClock.Instance, log);
        var locator = new OverridingAppLocator(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            () => HostPath);

        var manager = new AppManager(locator, process, resolver, driver, Profile, SystemClock.Instance, log);

        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath,
            WorkingDirectory = HostDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(output);
        foreach (var argument in extraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Harness(manager, process, log, output, startInfo, this);
    }

    public HostRun StartHost(string[] extraArguments)
    {
        var output = Path.Combine(Path.GetTempPath(), "ibkr-testhost-" + Guid.NewGuid().ToString("N") + ".json");
        _outputFiles.Add(output);

        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath,
            WorkingDirectory = HostDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(output);
        foreach (var argument in extraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the test host");
        _launched.Add(process);
        WaitUntil(() => process.MainWindowHandle != IntPtr.Zero, TimeSpan.FromSeconds(30));
        return new HostRun(process, output);
    }

    public void Track(Process process) => _launched.Add(process);

    public static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("condition was not met in time");
    }

    public static JsonElement ReadPayload(string path)
    {
        WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        // The host writes in one call, but retry in case the read races the flush.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Thread.Sleep(100);
            }
        }

        throw new InvalidOperationException($"could not read {path}");
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                key?.DeleteSubKeyTree("IbkrDesktopManagerTestHost", throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
                // Cleanup is best effort.
            }
        }

        foreach (var process in _launched)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var file in _outputFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Ignore.
            }
        }
    }
}

public sealed record HostRun(Process Process, string OutputPath);

public sealed record Harness(
    AppManager Manager,
    Win32ProcessController Process,
    RecordingLog Log,
    string OutputPath,
    ProcessStartInfo StartInfo,
    TestHostFixture Fixture)
{
    public void EnsureHostStarted()
    {
        var host = System.Diagnostics.Process.Start(StartInfo)
                   ?? throw new InvalidOperationException("could not start the test host");
        Fixture.Track(host);
        TestHostFixture.WaitUntil(() => host.MainWindowHandle != IntPtr.Zero, TimeSpan.FromSeconds(30));
    }
}
