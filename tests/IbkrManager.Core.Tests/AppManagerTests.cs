using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class AppManagerTests
{
    [Fact]
    public async Task Initialize_reports_not_installed_when_the_app_is_missing()
    {
        var harness = new Harness(installed: false);

        var state = await harness.Manager.InitializeAsync();

        Assert.Equal(AppStatus.NotInstalled, state.Status);
        Assert.Null(state.Installation);
    }

    [Fact]
    public async Task Initialize_reports_stopped_when_installed_and_not_running()
    {
        var harness = new Harness();

        var state = await harness.Manager.InitializeAsync();

        Assert.Equal(AppStatus.Stopped, state.Status);
        Assert.Equal(TestProfile.Installation, state.Installation);
    }

    [Fact]
    public async Task Start_launches_the_app_and_reports_running()
    {
        var harness = new Harness();

        var state = await harness.Manager.StartAsync();

        Assert.Equal(1, harness.Process.StartCount);
        Assert.Equal(AppStatus.Running, state.Status);
        Assert.Equal(4242, state.ProcessId);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task Start_when_not_installed_fails_with_a_helpful_error()
    {
        var harness = new Harness(installed: false);

        var state = await harness.Manager.StartAsync();

        Assert.Equal(0, harness.Process.StartCount);
        Assert.Contains("not installed", state.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_attaches_to_an_already_running_instance()
    {
        var harness = new Harness();
        harness.Process.IsRunning = true;
        harness.Process.HasVisibleWindow = true;
        harness.Process.MainWindowTitle = "IBKR Desktop Login";

        var state = await harness.Manager.StartAsync();

        Assert.Equal(0, harness.Process.StartCount);
        Assert.Equal(AppStatus.Running, state.Status);
        Assert.Contains("attached", state.LastAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoLogin_blocks_before_launching_when_the_password_is_missing()
    {
        var harness = new Harness(
            providers: [new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "user", null, "IBKR_PASSWORD")]);

        var state = await harness.Manager.AutoLoginAsync();

        Assert.Equal(0, harness.Process.StartCount);
        Assert.Equal(0, harness.Driver.CallCount);
        Assert.Contains("Auto Login blocked", state.LastError);
        Assert.Contains("IBKR_PASSWORD", state.LastError);
    }

    [Fact]
    public async Task AutoLogin_launches_a_stopped_app_then_types_the_credentials()
    {
        var harness = new Harness(
            providers: [new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "user", "secret-pass")]);

        var state = await harness.Manager.AutoLoginAsync();

        Assert.Equal(1, harness.Process.StartCount);
        Assert.Equal(1, harness.Driver.CallCount);
        Assert.Equal(1, harness.Process.FocusCount);
        Assert.Equal("user", harness.Driver.LastRequest!.Username.Reveal());
        Assert.Equal(AppStatus.Running, state.Status);
        Assert.NotNull(state.LastAttempt);
        Assert.True(state.LastAttempt!.AwaitingTwoFactor);
        Assert.Contains("2fa", state.LastAttempt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoLogin_surfaces_a_driver_failure_and_records_the_attempt()
    {
        var harness = new Harness(
            providers: [new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "user", "secret-pass")]);
        harness.Process.IsRunning = true;
        harness.Process.HasVisibleWindow = true;
        harness.Process.MainWindowTitle = "IBKR Desktop Login";
        harness.Driver.Result = LoginResult.Failed("the login window is still showing");

        var state = await harness.Manager.AutoLoginAsync();

        Assert.Equal(AppStatus.Running, state.Status);
        Assert.NotNull(state.LastAttempt);
        Assert.False(state.LastAttempt!.Success);
        Assert.Contains("Auto login failed", state.LastError);
        Assert.Contains("still showing", state.LastError);
    }

    [Fact]
    public async Task AutoLogin_with_the_manual_driver_reports_disabled()
    {
        var harness = new Harness(
            providers: [new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "user", "secret-pass")],
            driver: new ManualLoginDriver());

        var state = await harness.Manager.AutoLoginAsync();

        Assert.Equal(1, harness.Process.StartCount);
        Assert.Contains("disabled", state.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quit_closes_gracefully_when_the_app_exits()
    {
        var harness = new Harness();
        await harness.Manager.StartAsync();

        var state = await harness.Manager.QuitAsync();

        Assert.Equal(0, harness.Process.KillCount);
        Assert.Equal(AppStatus.Stopped, state.Status);
        Assert.Equal("Closed gracefully", state.LastAction);
    }

    [Fact]
    public async Task Quit_escalates_to_killing_the_process_tree()
    {
        var harness = new Harness();
        await harness.Manager.StartAsync();
        harness.Process.ExitOnWait = false;

        var state = await harness.Manager.QuitAsync();

        Assert.Equal(1, harness.Process.KillCount);
        Assert.Equal(AppStatus.Stopped, state.Status);
        Assert.Contains("Terminated", state.LastAction);
    }

    [Fact]
    public async Task Restart_closes_then_launches_again()
    {
        var harness = new Harness();
        await harness.Manager.StartAsync();

        var state = await harness.Manager.RestartAsync();

        Assert.Equal(2, harness.Process.StartCount);
        Assert.Equal(1, harness.Process.WaitForExitCount);
        Assert.Equal(AppStatus.Running, state.Status);
    }

    [Fact]
    public async Task Tick_only_reads_process_state_and_never_the_window()
    {
        // The manager is stateless about the app's UI: it does not inspect the window on ticks, so it
        // cannot keep the app busy or claim to know whether the user is logged in.
        var harness = new Harness();
        await harness.Manager.StartAsync();
        var readsAfterStart = harness.Process.TitleReads;

        for (var i = 0; i < 5; i++)
        {
            var state = await harness.Manager.TickAsync();
            Assert.Equal(AppStatus.Running, state.Status);
        }

        Assert.Equal(readsAfterStart, harness.Process.TitleReads);
    }

    [Fact]
    public async Task AutoLogin_explains_a_missing_login_screen()
    {
        var harness = new Harness(
            providers: [new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "user", "secret-pass")]);
        harness.Process.IsRunning = true;
        harness.Process.HasVisibleWindow = true;
        harness.Driver.Result = LoginResult.DoesNotApply("no login form was exposed through UI Automation");

        var state = await harness.Manager.AutoLoginAsync();

        Assert.Contains("login screen", state.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppStatus.Running, state.Status);
    }

    [Fact]
    public async Task State_changes_are_published_to_subscribers()
    {
        var harness = new Harness();
        var observed = new List<AppStatus>();
        harness.Manager.StateChanged += (_, state) => observed.Add(state.Status);

        await harness.Manager.StartAsync();

        Assert.Contains(AppStatus.Running, observed);
    }

    [Fact]
    public async Task Tick_never_blocks_behind_a_running_command()
    {
        var harness = new Harness();
        harness.Process.OnStart = () => true;

        var start = harness.Manager.StartAsync();
        var tick = harness.Manager.TickAsync();

        await Task.WhenAll(start, tick);
        Assert.Equal(AppStatus.Running, (await tick).Status);
    }

    private sealed class Harness
    {
        public Harness(
            AppInstallation? installation = null,
            bool installed = true,
            ICredentialProvider[]? providers = null,
            ILoginDriver? driver = null)
        {
            Installation = installed ? installation ?? TestProfile.Installation : null;
            Clock = new FakeClock();
            Process = new FakeProcessController(Clock);
            Driver = driver as FakeLoginDriver ?? new FakeLoginDriver();
            Resolver = new CredentialResolver(providers ?? []);
            Log = new RecordingLog();

            Manager = new AppManager(
                new FakeLocator(Installation),
                Process,
                Resolver,
                driver ?? Driver,
                TestProfile.Fast,
                Clock,
                Log);
        }

        public AppInstallation? Installation { get; }

        public FakeClock Clock { get; }

        public FakeProcessController Process { get; }

        public FakeLoginDriver Driver { get; }

        public CredentialResolver Resolver { get; }

        public RecordingLog Log { get; }

        public AppManager Manager { get; }
    }
}
