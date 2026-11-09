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
            Assert.Equal(AppStatus.AwaitingLogin, state.Status);
            Assert.NotNull(state.ProcessId);
            Assert.True(IsAlive(state.ProcessId!.Value));
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }

    [Fact]
    public async Task Auto_login_types_both_fields_and_reaches_the_two_factor_prompt()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var harness = fixture.CreateHarness("test-user", "test-password");
        try
        {
            await harness.Manager.InitializeAsync();
            var state = await harness.Manager.AutoLoginAsync();
            output.WriteLine(string.Join(Environment.NewLine, harness.Log.Messages));

            Assert.True(File.Exists(harness.OutputPath),
                "the stub never received a submit; log:\n" + string.Join(Environment.NewLine, harness.Log.Messages));
            var payload = TestHostFixture.ReadPayload(harness.OutputPath);
            Assert.Equal("test-user", payload.GetProperty("username").GetString());
            Assert.Equal("test-password", payload.GetProperty("password").GetString());
            Assert.Equal(AppStatus.AwaitingTwoFactor, state.Status);
            Assert.Contains("2FA", state.LastAction, StringComparison.OrdinalIgnoreCase);
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
    public async Task Quit_kills_the_process_tree_when_the_app_ignores_wm_close()
    {
        if (!WindowsRequired())
        {
            return;
        }

        var harness = fixture.CreateHarness("test-user", "test-password", "--ignore-close", "--spawn-child");
        try
        {
            await harness.Manager.InitializeAsync();
            await harness.Manager.AutoLoginAsync();

            Assert.True(File.Exists(harness.OutputPath),
                "the stub never received a submit; log:\n" + string.Join(Environment.NewLine, harness.Log.Messages));
            var payload = TestHostFixture.ReadPayload(harness.OutputPath);
            var childPid = payload.GetProperty("childPid").GetInt32();
            Assert.True(IsAlive(childPid), "the stub should have spawned a child process");

            var stopped = await harness.Manager.QuitAsync();

            Assert.Equal(AppStatus.Stopped, stopped.Status);
            Assert.Contains("Terminated", stopped.LastAction);
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
