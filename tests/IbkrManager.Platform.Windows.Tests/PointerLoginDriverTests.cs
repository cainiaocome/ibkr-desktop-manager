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
    }

    [Fact]
    public async Task Does_not_apply_when_the_session_cannot_deliver_input()
    {
        var harness = new Harness(LoginControls()) { CanInject = false };

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.NotApplicable);
        Assert.Contains("synthetic", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Mouse.Clicks);
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

        Assert.Equal("alice", harness.Ui.Values[0]);
        Assert.Equal("secret", harness.Ui.Values[1]);
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
    public async Task Fails_without_submitting_when_the_typed_username_does_not_reach_the_app()
    {
        // Reproduces the ValuePattern failure mode: the app never receives the characters.
        var harness = new Harness(LoginControls());
        harness.Ui.AcceptsTyping = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("did not receive the typed username", result.Message);
        Assert.Single(harness.Mouse.Clicks);
        Assert.DoesNotContain(harness.Mouse.Clicks, click => click.Y == 300);
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

        // The username must be restored after the failed attempt.
        Assert.Equal("alice", harness.Ui.Values[0]);
        Assert.Equal(0, harness.Ui.WaitCalls);
    }

    [Fact]
    public async Task Fails_when_windows_rejects_the_keystrokes()
    {
        var harness = new Harness(LoginControls());
        harness.Keyboard.RejectText = true;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("SendInput", result.Message);
        Assert.Equal(0, harness.Ui.WaitCalls);
    }

    [Fact]
    public async Task Fails_when_the_login_button_cannot_be_clicked()
    {
        var harness = new Harness(LoginControls());
        harness.Mouse.Reject = true;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("click", result.Message);
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
            Driver = new PointerLoginDriver(
                Ui,
                new StubProcess(),
                NullAppLog.Instance,
                Keyboard,
                Mouse,
                () => CanInject,
                Inspector);
        }

        public FakeUi Ui { get; }

        public RecordingKeyboard Keyboard { get; }

        public RecordingMouse Mouse { get; }

        public FakeInspector Inspector { get; } = new();

        public bool CanInject { get; init; } = true;

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

        /// <summary>False simulates a toolkit whose fields never see the synthetic keystrokes.</summary>
        public bool AcceptsTyping { get; set; } = true;

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
            var ui = Ui;
            if (ui is null || !ui.AcceptsTyping)
            {
                return true;
            }

            if (ui.Focus == 1 && !ui.AcceptsPasswordTyping)
            {
                return true;
            }

            ui.Values[ui.Focus] = (ui.Values.GetValueOrDefault(ui.Focus) ?? string.Empty) + text;
            return true;
        }

        public bool SendKeyPress(ushort key)
        {
            Events.Add("key:" + key);
            var ui = Ui;
            if (ui is null)
            {
                return true;
            }

            switch (key)
            {
                case 0x08: // Backspace
                    ui.Values[ui.Focus] = string.Empty;
                    break;
                case 0x09: // Tab
                    if (ui.TabMovesFocus)
                    {
                        ui.Focus = ui.Focus == 0 ? 1 : 0;
                    }

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

        public int LastError => Reject ? 5 : 0;

        public FakeUi? Ui { get; set; }

        public bool Click(int x, int y)
        {
            if (Reject)
            {
                return false;
            }

            Clicks.Add((x, y));
            var controls = Ui?.Controls;
            if (Ui is null || controls is null)
            {
                return true;
            }

            // A click focuses the field under the pointer, exactly like the real form.
            foreach (var (index, bounds) in new[]
                     {
                         (controls.UsernameIndex, controls.UsernameBounds),
                         (controls.PasswordIndex, controls.PasswordBounds),
                     })
            {
                if (!bounds.IsEmpty && Math.Abs(bounds.CenterY - y) <= Math.Max(1, bounds.Height / 2))
                {
                    Ui.Focus = index;
                    return true;
                }
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

        public IntPtr? MainWindowHandle => null;

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
