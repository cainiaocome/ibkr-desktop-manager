using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

public sealed class PointerLoginDriverTests
{
    private static readonly AppProfile Profile = AppProfile.Default with
    {
        LoginResultTimeout = TimeSpan.Zero,
        LoginAutomationIdPatterns = ["launcherLoginWindow"],
        LoginButtonNames = ["Log In"],
        TypingDelayMilliseconds = 0,
    };

    private static LoginRequest Request() => new(new Secret("alice"), new Secret("secret"), Profile);

    private static UiLoginControls LoginControls(
        UiBounds? username = null,
        UiBounds? submit = null) =>
        new(
            0,
            1,
            CanSubmit: true,
            "two fields",
            username ?? new UiBounds(60, 200, 100, 20),
            new UiBounds(60, 230, 100, 20),
            submit ?? new UiBounds(60, 290, 100, 20));

    [Fact]
    public async Task Does_not_apply_when_no_login_form_is_exposed()
    {
        var harness = new Harness(controls: null);

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.NotApplicable);
        Assert.False(result.Success);
        Assert.Empty(harness.Mouse.Clicks);
        Assert.Empty(harness.Keyboard.Events);
        Assert.Empty(harness.Posted.Events);
    }

    [Fact]
    public async Task Uses_sendinput_on_an_interactive_desktop_and_never_posts()
    {
        var harness = new Harness(LoginControls());

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Empty(harness.Posted.Events);
        Assert.Equal("alice", harness.Ui.Values[0]);
        Assert.Equal("secret", harness.Ui.Values[1]);
    }

    [Fact]
    public async Task Uses_posted_messages_when_the_session_has_no_input_desktop()
    {
        var harness = new Harness(LoginControls()) { Interactive = false };

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Empty(harness.Mouse.Clicks);
        Assert.Empty(harness.Keyboard.Events);
        Assert.Equal("alice", harness.Ui.Values[0]);
        Assert.Equal("secret", harness.Ui.Values[1]);
    }

    [Fact]
    public async Task Falls_back_to_posted_messages_when_sendinput_does_not_land()
    {
        // The real failure mode on a service session: SendInput is discarded without an error.
        var harness = new Harness(LoginControls());
        harness.Ui.RealInputWorks = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.NotEmpty(harness.Posted.Events);
        Assert.Equal("alice", harness.Ui.Values[0]);
        Assert.Equal("secret", harness.Ui.Values[1]);
    }

    [Fact]
    public async Task Clicks_the_username_tabs_to_the_password_and_clicks_the_button()
    {
        var harness = new Harness(LoginControls());

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Empty(harness.Ui.SetCalls);

        // Username is clicked, the password is reached with Tab (like a human), then the button.
        Assert.Equal([(110, 210), (110, 300)], harness.Mouse.Clicks);

        Assert.Equal(
            [
                "selectall", "key:8", "text:a", "text:l", "text:i", "text:c", "text:e",
                "key:9",
                "selectall", "key:8", "text:s", "text:e", "text:c", "text:r", "text:e", "text:t",
            ],
            harness.Keyboard.Events);

        Assert.Equal(1, harness.Ui.WaitCalls);
        Assert.Contains("2FA", result.Message);
    }

    [Fact]
    public async Task Uses_the_rectangle_reported_by_ui_automation()
    {
        var harness = new Harness(LoginControls(username: new UiBounds(10, 20, 100, 30)));

        _ = await harness.Driver.LoginAsync(Request());

        Assert.Equal((60, 35), harness.Mouse.Clicks[0]);
    }

    [Fact]
    public async Task Clicks_the_password_field_when_tab_does_not_move_focus()
    {
        // If Tab is swallowed, the password would land in the username field; the driver must notice
        // (username no longer matches) and click the password field instead.
        var harness = new Harness(LoginControls());
        harness.Ui.TabMovesFocus = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Contains((110, 240), harness.Mouse.Clicks);
        Assert.Equal("alice", harness.Ui.Values[0]);
        Assert.Equal("secret", harness.Ui.Values[1]);
    }

    [Fact]
    public async Task Fails_without_submitting_when_no_strategy_reaches_the_app()
    {
        var harness = new Harness(LoginControls());
        harness.Ui.RealInputWorks = false;
        harness.Ui.PostedInputWorks = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("did not receive the typed username", result.Message);
        Assert.Contains("sendinput", result.Message);
        Assert.Contains("posted", result.Message);
        Assert.Equal(0, harness.Ui.WaitCalls);
    }

    [Fact]
    public async Task Fails_without_submitting_when_the_password_field_stays_empty()
    {
        var harness = new Harness(LoginControls());
        harness.Ui.AcceptsPasswordTyping = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("password field did not receive", result.Message);
        Assert.Equal(0, harness.Ui.WaitCalls);
    }

    [Fact]
    public async Task Fails_when_windows_rejects_the_keystrokes()
    {
        var harness = new Harness(LoginControls());
        harness.Keyboard.RejectText = true;
        harness.Posted.Reject = true;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("could not type the credentials", result.Message);
        Assert.Equal(0, harness.Ui.WaitCalls);
    }

    [Fact]
    public async Task Fails_when_the_login_button_cannot_be_clicked()
    {
        // Fields land normally, but the click on the button is refused.
        var harness = new Harness(LoginControls());
        harness.Mouse.RejectY = 300;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("login button", result.Message);
    }

    [Fact]
    public async Task Presses_enter_when_the_button_has_no_rectangle()
    {
        var harness = new Harness(LoginControls(submit: new UiBounds(0, 0, 0, 0)));

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Single(harness.Mouse.Clicks);
        Assert.Contains("key:13", harness.Keyboard.Events);
    }

    [Fact]
    public async Task Presses_enter_with_posted_messages_when_the_button_has_no_rectangle()
    {
        var harness = new Harness(LoginControls(submit: new UiBounds(0, 0, 0, 0))) { Interactive = false };

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Contains("key:13", harness.Posted.Events);
    }

    [Fact]
    public async Task Fails_when_the_form_stays_on_screen()
    {
        var harness = new Harness(LoginControls());
        harness.Ui.FormDisappears = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("still showing", result.Message);
    }

    [Fact]
    public async Task Includes_the_apps_own_message_in_the_failure()
    {
        var harness = new Harness(LoginControls());
        harness.Ui.FormDisappears = false;
        harness.Inspector.Elements =
        [
            new UiElementInfo("Text", "Please enter your username and password", string.Empty, false),
            new UiElementInfo("Text", "secret-alice-value", string.Empty, false),
        ];

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("Please enter your username and password", result.Message);
        Assert.DoesNotContain("secret-alice-value", result.Message);
    }

    [Fact]
    public async Task Reports_a_rejection_instead_of_waiting_for_two_factor_when_the_form_disappears()
    {
        // A wrong password also removes the login form: the app's message must be reported.
        var harness = new Harness(LoginControls());
        harness.Inspector.Elements =
        [
            new UiElementInfo("Text", "Invalid username or password", string.Empty, false),
        ];

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.False(result.ReachedTwoFactor);
        Assert.Contains("Invalid username or password", result.Message);
    }

    [Fact]
    public async Task Does_not_click_fields_without_a_screen_rectangle()
    {
        var harness = new Harness(LoginControls(username: new UiBounds(0, 0, 0, 0)));

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("cannot be clicked", result.Message);
        Assert.Empty(harness.Mouse.Clicks);
    }

    private sealed class Harness
    {
        public Harness(UiLoginControls? controls)
        {
            Ui = new FakeUi { Controls = controls };
            Keyboard = new RecordingKeyboard { Ui = Ui };
            Mouse = new RecordingMouse { Ui = Ui };
            Posted = new RecordingPostedInput { Ui = Ui };
            Driver = new PointerLoginDriver(
                Ui,
                new StubProcess(),
                NullAppLog.Instance,
                Keyboard,
                Mouse,
                Posted,
                () => Interactive,
                Inspector);
        }

        public FakeUi Ui { get; }

        public RecordingKeyboard Keyboard { get; }

        public RecordingMouse Mouse { get; }

        public RecordingPostedInput Posted { get; }

        public FakeInspector Inspector { get; } = new();

        public bool Interactive { get; init; } = true;

        public PointerLoginDriver Driver { get; }
    }

    private sealed class FakeInspector : IWindowInspector
    {
        public IReadOnlyList<UiElementInfo> Elements { get; set; } = [];

        public UiObservation? Observe() => new("IBKR Desktop", Elements);
    }

    /// <summary>Models the QML form: what has focus, and what each field currently contains.</summary>
    private sealed class FakeUi : IUiLoginUi
    {
        public Dictionary<int, string> Values { get; } = [];

        public UiLoginControls? Controls { get; init; }

        public int Focus { get; set; }

        public bool FormDisappears { get; set; } = true;

        /// <summary>False simulates a session where Windows discards SendInput.</summary>
        public bool RealInputWorks { get; set; } = true;

        /// <summary>False simulates an app that ignores posted messages.</summary>
        public bool PostedInputWorks { get; set; } = true;

        public bool AcceptsPasswordTyping { get; set; } = true;

        public bool TabMovesFocus { get; set; } = true;

        public List<(int Index, string Value)> SetCalls { get; } = [];

        public int WaitCalls { get; private set; }

        public UiLoginControls? FindLoginControls() => Controls;

        public string? GetFieldValue(int index) => Values.GetValueOrDefault(index) ?? string.Empty;

        public int? GetFieldLength(int index) => Values.GetValueOrDefault(index)?.Length ?? 0;

        public bool SetField(int index, string value)
        {
            SetCalls.Add((index, value));
            Values[index] = value;
            return true;
        }

        public bool Submit() => false;

        public bool WaitUntilLoginControlsGone(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            WaitCalls++;
            return FormDisappears;
        }

        /// <summary>Which field the given screen position would focus, or -1.</summary>
        public int FieldAt(int y)
        {
            var controls = Controls;
            if (controls is null)
            {
                return -1;
            }

            foreach (var (index, bounds) in new[]
                     {
                         (controls.UsernameIndex, controls.UsernameBounds),
                         (controls.PasswordIndex, controls.PasswordBounds),
                     })
            {
                if (!bounds.IsEmpty && Math.Abs(bounds.CenterY - y) <= Math.Max(1, bounds.Height / 2))
                {
                    return index;
                }
            }

            return -1;
        }

        public void Type(string text)
        {
            if (Focus == 1 && !AcceptsPasswordTyping)
            {
                return;
            }

            Values[Focus] = (Values.GetValueOrDefault(Focus) ?? string.Empty) + text;
        }

        public void Backspace() => Values[Focus] = string.Empty;

        public void Tab()
        {
            if (TabMovesFocus)
            {
                Focus = Focus == 0 ? 1 : 0;
            }
        }
    }

    private sealed class RecordingKeyboard : IKeyboardInput
    {
        public List<string> Events { get; } = [];

        public bool RejectText { get; set; }

        public int LastError => 0;

        public FakeUi? Ui { get; set; }

        public bool SendText(string text)
        {
            if (RejectText)
            {
                return false;
            }

            Events.Add("text:" + text);
            if (Ui is { RealInputWorks: true })
            {
                Ui.Type(text);
            }

            return true;
        }

        public bool SendKeyPress(ushort key)
        {
            Events.Add("key:" + key);
            if (Ui is not { RealInputWorks: true })
            {
                return true;
            }

            switch (key)
            {
                case 0x08:
                    Ui.Backspace();
                    break;
                case 0x09:
                    Ui.Tab();
                    break;
            }

            return true;
        }

        public bool SendSelectAll()
        {
            Events.Add("selectall");
            return true;
        }
    }

    private sealed class RecordingMouse : IMouseInput
    {
        public List<(int X, int Y)> Clicks { get; } = [];

        public bool Reject { get; set; }

        /// <summary>Rejects only clicks at this screen Y (to fail the submit click alone).</summary>
        public int? RejectY { get; set; }

        public int LastError => Reject || RejectY is not null ? 5 : 0;

        public FakeUi? Ui { get; set; }

        public bool Click(int x, int y)
        {
            if (Reject || (RejectY is { } rejectY && Math.Abs(rejectY - y) <= 10))
            {
                return false;
            }

            Clicks.Add((x, y));
            if (Ui is not { RealInputWorks: true })
            {
                return true;
            }

            var field = Ui.FieldAt(y);
            if (field >= 0)
            {
                Ui.Focus = field;
            }

            return true;
        }
    }

    private sealed class RecordingPostedInput : IPostedInput
    {
        public List<string> Events { get; } = [];

        public bool Reject { get; set; }

        public FakeUi? Ui { get; set; }

        public bool Click(int x, int y)
        {
            if (Reject)
            {
                return false;
            }

            Events.Add($"click:{x},{y}");
            if (Ui is not { PostedInputWorks: true })
            {
                return true;
            }

            var field = Ui.FieldAt(y);
            if (field >= 0)
            {
                Ui.Focus = field;
            }

            return true;
        }

        public bool Text(char character)
        {
            if (Reject)
            {
                return false;
            }

            Events.Add("char:" + character);
            if (Ui is { PostedInputWorks: true })
            {
                Ui.Type(character.ToString());
            }

            return true;
        }

        public bool KeyPress(ushort virtualKey)
        {
            if (Reject)
            {
                return false;
            }

            Events.Add("key:" + virtualKey);
            if (Ui is not { PostedInputWorks: true })
            {
                return true;
            }

            switch (virtualKey)
            {
                case 0x08:
                    Ui.Backspace();
                    break;
                case 0x09:
                    Ui.Tab();
                    break;
            }

            return true;
        }
    }

    private sealed class StubProcess : IProcessController
    {
        public bool IsRunning => true;

        public int? ProcessId => 4321;

        public DateTimeOffset? StartedAt => DateTimeOffset.UtcNow;

        public string? MainWindowTitle => "IBKR Desktop";

        public IntPtr? MainWindowHandle => (IntPtr)1234;

        public bool HasVisibleWindow => true;

        public Task StartAsync(AppInstallation installation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryFocusMainWindow() => true;

        public Task<bool> CloseMainWindowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task KillAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
