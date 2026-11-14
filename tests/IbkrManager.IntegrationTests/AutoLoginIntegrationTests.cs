using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

[CollectionDefinition("windows-ui", DisableParallelization = true)]
public sealed class WindowsUiCollection : ICollectionFixture<TestHostFixture>
{
}

/// <summary>
/// End-to-end checks against a real Windows desktop: registry discovery, process launch, window
/// classification, SendInput typing (focus + Tab order + Enter), graceful close and process-tree kill.
/// They are no-ops on non-Windows hosts, so the normal cross-platform test run stays green.
/// </summary>
[Collection("windows-ui")]
public sealed class AutoLoginIntegrationTests(TestHostFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public void Registry_discovery_finds_the_installed_app()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var installation = new WindowsAppLocator(fixture.Profile, UninstallRegistry()).Locate();

        Assert.NotNull(installation);
        Assert.Equal(fixture.HostPath, installation!.ExecutablePath, ignoreCase: true);
        Assert.Equal("9.9.9", installation.Version);
    }

    [Fact]
    public async Task Start_launches_the_app_and_reports_the_login_screen()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var harness = fixture.CreateHarness();
        try
        {
            await harness.Manager.InitializeAsync();
            var state = await harness.Manager.StartAsync();

            output.WriteLine(string.Join(Environment.NewLine, harness.Log.Messages));
            Assert.Equal(AppStatus.Running, state.Status);
            Assert.NotNull(state.ProcessId);
            Assert.True(IsAlive(state.ProcessId!.Value));
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }

    [Fact]
    public async Task Auto_login_types_the_selected_account_and_not_the_first_one()
    {
        if (!WindowsRequired())
        {
            return;
        }

        if (!InteractiveSession.IsInteractive())
        {
            // The stub only takes typed input through real keystrokes; service sessions discard them.
            output.WriteLine("SKIPPED: typing needs an interactive desktop ({0}).", InteractiveSession.Describe());
            return;
        }

        var harness = fixture.CreateHarness(
            accounts:
            [
                ("Main", "main-user", "main-password"),
                ("Paper", "paper-user", "paper-password"),
            ],
            selectAlias: "Paper",
            pointerDriverForManager: true);
        try
        {
            await harness.Manager.InitializeAsync();
            var state = await harness.Manager.AutoLoginAsync();
            Assert.Equal("Paper", state.SelectedAccountAlias);

            var typed = default(System.Text.Json.JsonElement);
            TestHostFixture.WaitUntil(
                () =>
                {
                    try
                    {
                        typed = TestHostFixture.ReadPayload(harness.OutputPath);
                        // Wait for the submit so the payload has the complete username.
                        return typed.GetProperty("submitted").GetBoolean();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(30));

            output.WriteLine(string.Join(Environment.NewLine, harness.Log.Messages));
            Assert.Equal("paper-user", typed.GetProperty("username").GetString());
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }

    [Fact]
    public async Task Auto_login_blocks_when_no_credentials_are_configured()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var harness = fixture.CreateHarness();
        try
        {
            await harness.Manager.InitializeAsync();
            var state = await harness.Manager.AutoLoginAsync();

            Assert.Contains("Auto Login blocked", state.LastError);
            Assert.Null(state.ProcessId);
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }

    [Fact]
    public async Task Start_attaches_to_an_instance_it_did_not_launch()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var host = fixture.StartHost([]);
        var harness = fixture.CreateHarness();
        try
        {
            await harness.Manager.InitializeAsync();
            var state = await harness.Manager.StartAsync();

            Assert.Equal(host.Process.Id, state.ProcessId);
            Assert.Contains("attached", state.LastAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
            host.Process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Quit_closes_gracefully_when_the_app_honours_wm_close()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var harness = fixture.CreateHarness();
        AppState started;
        try
        {
            await harness.Manager.InitializeAsync();
            started = await harness.Manager.StartAsync();
        }
        catch
        {
            await harness.Manager.QuitAsync(force: true);
            throw;
        }

        var stopped = await harness.Manager.QuitAsync();

        Assert.Equal(AppStatus.Stopped, stopped.Status);
        Assert.Equal("Closed gracefully", stopped.LastAction);
        Assert.False(IsAlive(started.ProcessId!.Value));
    }

    [Fact]
    public async Task Quit_leaves_an_app_that_ignores_wm_close_running_and_force_quit_kills_it()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var harness = fixture.CreateHarness(ignoreClose: true, spawnChild: true);
        try
        {
            await harness.Manager.InitializeAsync();
            var started = await harness.Manager.StartAsync();

            var payload = TestHostFixture.ReadPayload(harness.OutputPath);
            output.WriteLine(string.Join(Environment.NewLine, harness.Log.Messages));
            Assert.True(IsAlive(started.ProcessId!.Value), "the stub should still be running after ignoring WM_CLOSE");
            var childPid = payload.GetProperty("childPid").GetInt32();
            Assert.True(IsAlive(childPid), "the stub should have spawned a child process");

            // Quit is graceful and never terminates: the stub refuses to close, so it must survive and
            // the manager must say so.
            var quit = await harness.Manager.QuitAsync();

            var closeNote = ReadCloseNote(harness.OutputPath);
            Assert.Equal(AppStatus.Running, quit.Status);
            Assert.True(quit.LastAction!.Contains("did not exit after the close request", StringComparison.OrdinalIgnoreCase),
                $"expected an honest 'still running' report, got '{quit.LastAction}'; stub close note: {closeNote}; log:\n" +
                string.Join(Environment.NewLine, harness.Log.Messages));
            Assert.True(IsAlive(started.ProcessId!.Value), "Quit must not terminate the app");
            Assert.True(IsAlive(childPid), "Quit must not touch the child process");

            // Force Quit is the explicit termination, and it takes the whole tree down.
            var stopped = await harness.Manager.QuitAsync(force: true);

            Assert.Equal(AppStatus.Stopped, stopped.Status);
            Assert.True(stopped.LastAction!.Contains("Terminated", StringComparison.OrdinalIgnoreCase),
                $"expected a kill, got '{stopped.LastAction}'");
            Assert.False(IsAlive(started.ProcessId!.Value));
            TestHostFixture.WaitUntil(() => !IsAlive(childPid), TimeSpan.FromSeconds(10));
            Assert.False(IsAlive(childPid), "the child process should have been killed with the tree");
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }

    /// <summary>Skips on non-Windows, but fails loudly on Windows if the stub is missing.</summary>
    private bool WindowsRequired()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        Assert.True(fixture.HostAvailable, $"test host was not copied to {fixture.HostPath}");
        return true;
    }

    private static string ReadCloseNote(string outputPath)
    {
        var path = outputPath + ".close";
        return File.Exists(path) ? File.ReadAllText(path) : "(the app never saw a close request)";
    }

    private static UninstallRegistry UninstallRegistry() => new();

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
