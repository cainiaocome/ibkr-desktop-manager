using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

/// <summary>
/// Verifies the keystroke sequence and outcome handling of the login driver. The keyboard is faked,
/// so this runs on any OS; actual delivery through <c>SendInput</c> needs an interactive Windows
/// session (see the integration tests and docs/usage.md).
/// </summary>
public sealed class SendInputLoginDriverTests
{
    private static readonly AppProfile BaseProfile = AppProfile.Default with
    {
        DisplayName = "IBKR Desktop",
        LoginWindowTitlePatterns = ["- Login"],
        TwoFactorWindowTitlePatterns = ["Security Code"],
        LoggedInWindowTitlePatterns = ["Portfolio"],
        LoginResultTimeout = TimeSpan.FromSeconds(5),
        TypingDelayMilliseconds = 0,
        SubmitAfterPassword = true,
    };

    [Fact]
    public async Task Types_username_then_tab_then_password_then_enter()
    {
        var harness = new Harness();
        harness.Keyboard.OnKey = key =>
        {
            if (key == 13)
            {
                harness.Process.MainWindowTitle = "IBKR Desktop Security Code";
            }
        };

        var result = await harness.Driver.LoginAsync(harness.Request());

        Assert.True(result.Success);
        Assert.True(harness.Process.Focused);
        Assert.Equal(
            ["select-all", "key:8", "text:alice", "key:9", "text:secret", "key:13"],
            harness.Keyboard.Events);
    }

    [Fact]
    public async Task Reports_the_two_factor_prompt_when_the_app_shows_it()
    {
        var harness = new Harness();
        harness.Keyboard.OnKey = key =>
        {
            if (key == 13)
            {
                harness.Process.MainWindowTitle = "IBKR Desktop Security Code";
            }
        };

        var result = await harness.Driver.LoginAsync(harness.Request());

        Assert.True(result.ReachedTwoFactor);
        Assert.Equal("sendinput", harness.Driver.Name);
        Assert.Contains("2FA", result.Message);
    }

    [Fact]
    public async Task Reports_success_when_the_app_goes_straight_to_the_portfolio_window()
    {
        var harness = new Harness();
        harness.Keyboard.OnKey = key =>
        {
            if (key == 13)
            {
                harness.Process.MainWindowTitle = "IBKR Desktop Portfolio";
            }
        };

        var result = await harness.Driver.LoginAsync(harness.Request());

        Assert.True(result.Success);
        Assert.False(result.ReachedTwoFactor);
    }

    [Fact]
    public async Task Fails_when_the_login_window_never_changes()
    {
        var harness = new Harness(loginTimeout: TimeSpan.Zero);

        var result = await harness.Driver.LoginAsync(harness.Request());

        Assert.False(result.Success);
        Assert.Contains("login window is still showing", result.Message);
    }

    [Fact]
    public async Task Fails_with_the_sendinput_error_when_injection_is_refused()
    {
        var harness = new Harness();
        harness.Keyboard.RejectText = true;
        harness.Keyboard.ErrorCode = 5;

        var result = await harness.Driver.LoginAsync(harness.Request());

        Assert.False(result.Success);
        Assert.Contains("SendInput error 5", result.Message);
    }

    [Fact]
    public async Task Fails_when_the_password_field_cannot_be_reached()
    {
        var harness = new Harness();
        harness.Keyboard.RejectKey = 9;
        harness.Keyboard.ErrorCode = 5;

        var result = await harness.Driver.LoginAsync(harness.Request());

        Assert.False(result.Success);
        Assert.Contains("password field", result.Message);
    }

    [Fact]
    public async Task Skips_enter_when_submit_after_password_is_disabled()
    {
        var harness = new Harness();
        var profile = BaseProfile with { SubmitAfterPassword = false, LoginResultTimeout = TimeSpan.Zero };
        var request = new LoginRequest(new Secret("alice"), new Secret("secret"), profile);

        _ = await harness.Driver.LoginAsync(request);

        Assert.DoesNotContain("key:13", harness.Keyboard.Events);
    }

    private sealed class Harness
    {
        public Harness(TimeSpan? loginTimeout = null)
        {
            Process = new FakeProcessController
            {
                HasVisibleWindow = true,
                MainWindowTitle = "IBKR Desktop - Login",
            };
            Keyboard = new RecordingKeyboard();
            Profile = loginTimeout is null ? BaseProfile : BaseProfile with { LoginResultTimeout = loginTimeout.Value };
            Driver = new SendInputLoginDriver(Process, SystemClock.Instance, NullAppLog.Instance, Keyboard);
        }

        public FakeProcessController Process { get; }

        public RecordingKeyboard Keyboard { get; }

        public AppProfile Profile { get; }

        public SendInputLoginDriver Driver { get; }

        public LoginRequest Request() => new(new Secret("alice"), new Secret("secret"), Profile);
    }

    private sealed class RecordingKeyboard : IKeyboardInput
    {
        public List<string> Events { get; } = [];

        public bool RejectText { get; set; }

        public ushort? RejectKey { get; set; }

        public int ErrorCode { get; set; }

        public Action<ushort>? OnKey { get; set; }

        public int LastError { get; private set; }

        public bool SendText(string text)
        {
            if (RejectText)
            {
                LastError = ErrorCode;
                return false;
            }

            Events.Add("text:" + text);
            return true;
        }

        public bool SendKeyPress(ushort key)
        {
            if (RejectKey == key)
            {
                LastError = ErrorCode;
                return false;
            }

            Events.Add("key:" + key);
            OnKey?.Invoke(key);
            return true;
        }

        public bool SendSelectAll()
        {
            Events.Add("select-all");
            return true;
        }
    }

    private sealed class FakeProcessController : IProcessController
    {
        public bool IsRunning => true;

        public int? ProcessId => 4321;

        public DateTimeOffset? StartedAt => DateTimeOffset.UtcNow;

        public string? MainWindowTitle { get; set; }

        public IntPtr? MainWindowHandle => null;

        public bool HasVisibleWindow { get; set; }

        public bool Focused { get; private set; }

        public Task StartAsync(AppInstallation installation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool TryFocusMainWindow()
        {
            Focused = true;
            return true;
        }

        public Task<bool> CloseMainWindowAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task KillAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
