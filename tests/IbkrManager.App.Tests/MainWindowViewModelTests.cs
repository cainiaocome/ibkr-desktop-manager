using IbkrManager.App.ViewModels;
using Xunit;

namespace IbkrManager.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task Not_installed_disables_start_and_reports_the_path_problem()
    {
        var harness = new Harness(States.NotInstalled);

        await harness.ViewModel.InitializeAsync();

        Assert.Equal("Not installed", harness.ViewModel.StatusText);
        Assert.Contains("was not found", harness.ViewModel.StatusDetail);
        Assert.False(harness.ViewModel.CanStart);
        Assert.False(harness.ViewModel.CanAutoLogin);
        Assert.False(harness.ViewModel.CanQuit);
    }

    [Fact]
    public async Task Stopped_app_enables_start_and_auto_login_but_not_quit()
    {
        var harness = new Harness(States.Stopped);

        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.CanStart);
        Assert.True(harness.ViewModel.CanAutoLogin);
        Assert.False(harness.ViewModel.CanQuit);
        Assert.Equal("v0.12.0", harness.ViewModel.VersionText);
    }

    [Fact]
    public async Task Running_app_enables_quit_and_shows_pid_and_uptime()
    {
        var clock = new FakeClock();
        var harness = new Harness(States.Running with { StartedAt = clock.UtcNow.AddMinutes(-2) }, clock);

        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.CanQuit);
        Assert.False(harness.ViewModel.CanStart);
        Assert.Contains("PID 4242", harness.ViewModel.StatusDetail);
        Assert.Contains("02:00", harness.ViewModel.StatusDetail);
        Assert.Contains("Auto Login", harness.ViewModel.LoginStateText);
    }

    [Fact]
    public async Task Error_from_the_manager_is_shown_in_the_banner()
    {
        var harness = new Harness(States.Stopped with
        {
            LastError = "Auto Login blocked: IBKR_PASSWORD is not set",
            Status = AppStatus.Failed,
        });

        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.HasError);
        Assert.Contains("Auto Login blocked", harness.ViewModel.ErrorText);
        Assert.Equal("Error", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Credential_summary_shows_presence_and_hints_without_values()
    {
        var credentials = new ResolvedCredentials(
            new Secret("alice-zz"),
            null,
            CredentialSource.Session,
            CredentialSource.None,
            passwordHint: "IBKR_PASSWORD is not set");

        var harness = new Harness(States.Stopped with { Credentials = credentials });

        await harness.ViewModel.InitializeAsync();

        Assert.Equal("username ✓   password ✗", harness.ViewModel.CredentialsText);
        Assert.Contains("IBKR_PASSWORD is not set", harness.ViewModel.CredentialsDetail);
        Assert.DoesNotContain("alice-zz", harness.ViewModel.CredentialsText);
        Assert.DoesNotContain("alice-zz", harness.ViewModel.CredentialsDetail);
    }

    [Fact]
    public async Task Complete_credentials_show_the_source()
    {
        var credentials = new ResolvedCredentials(
            new Secret("alice-zz"),
            new Secret("pw"),
            CredentialSource.ProcessEnvironment,
            CredentialSource.CredentialManager);

        var harness = new Harness(States.Stopped with { Credentials = credentials });

        await harness.ViewModel.InitializeAsync();

        Assert.Contains("env", harness.ViewModel.CredentialsDetail);
        Assert.Contains("credential manager", harness.ViewModel.CredentialsDetail);
    }

    [Fact]
    public async Task Commands_call_the_manager()
    {
        var harness = new Harness(States.Stopped);
        await harness.ViewModel.InitializeAsync();

        await harness.ViewModel.StartCommand.ExecuteAsync();
        await harness.ViewModel.AutoLoginCommand.ExecuteAsync();
        await harness.ViewModel.RestartCommand.ExecuteAsync();

        Assert.Equal(1, harness.Manager.StartCalls);
        Assert.Equal(1, harness.Manager.AutoLoginCalls);
        Assert.Equal(1, harness.Manager.RestartCalls);
    }

    [Fact]
    public async Task Quit_command_is_blocked_when_not_running()
    {
        var harness = new Harness(States.Stopped);
        await harness.ViewModel.InitializeAsync();

        await harness.ViewModel.QuitCommand.ExecuteAsync();

        Assert.Equal(0, harness.Manager.QuitCalls);
    }

    [Fact]
    public async Task Refresh_re_reads_manager_state()
    {
        var harness = new Harness(States.Stopped);
        await harness.ViewModel.InitializeAsync();
        harness.Manager.State = States.Running;

        await harness.ViewModel.RefreshAsync();

        Assert.Equal("Running", harness.ViewModel.StatusText);
        Assert.Equal(1, harness.Manager.TickCalls);
    }

    [Fact]
    public async Task Open_credentials_raises_the_dialog_request()
    {
        var harness = new Harness(States.Stopped);
        await harness.ViewModel.InitializeAsync();
        var raised = 0;
        harness.ViewModel.CredentialsRequested += (_, _) => raised++;

        harness.ViewModel.OpenCredentialsCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Open_logs_uses_the_runtime()
    {
        var harness = new Harness(States.Stopped);
        await harness.ViewModel.InitializeAsync();

        harness.ViewModel.OpenLogsCommand.Execute(null);

        Assert.Single(harness.Runtime.Opened);
    }

    private sealed class Harness
    {
        public Harness(AppState state, FakeClock? clock = null)
        {
            Clock = clock ?? new FakeClock();
            Manager = new FakeAppManager { State = state };
            Runtime = new FakeRuntime(Manager);
            ViewModel = new MainWindowViewModel(Manager, Runtime, Clock);
        }

        public FakeClock Clock { get; }

        public FakeAppManager Manager { get; }

        public FakeRuntime Runtime { get; }

        public MainWindowViewModel ViewModel { get; }
    }
}
