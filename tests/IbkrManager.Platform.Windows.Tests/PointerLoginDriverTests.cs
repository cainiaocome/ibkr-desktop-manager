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

    private static LoginRequest Request(AppProfile? profile = null) =>
        new(new Secret("alice"), new Secret("secret"), profile ?? Profile);

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
    public async Task Clicks_each_field_and_types_it_instead_of_setting_values()
    {
        var harness = new Harness(LoginControls());

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor, result.Message);
        Assert.Empty(harness.Ui.SetCalls);

        // Username field centre, then password field centre, then the login button centre.
        Assert.Equal([(110, 210), (110, 240), (110, 300)], harness.Mouse.Clicks);

        // Each field is cleared with Ctrl+A and Backspace before the value is typed.
        Assert.Equal(
            [
                "selectall", "key:8", "text:a", "text:l", "text:i", "text:c", "text:e",
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

        Assert.Equal(60, harness.Mouse.Clicks[0].X);
        Assert.Equal(35, harness.Mouse.Clicks[0].Y);
    }

    [Fact]
    public async Task Fails_without_submitting_when_the_typed_username_does_not_reach_the_app()
    {
        // Reproduces the ValuePattern failure mode: the field stays empty, so submitting is pointless.
        var harness = new Harness(LoginControls());
        harness.Ui.AcceptsTyping = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("did not receive the typed username", result.Message);
        Assert.Single(harness.Mouse.Clicks);
        Assert.DoesNotContain("text:s", harness.Keyboard.Events);
        Assert.Equal(0, harness.Ui.WaitCalls);
    }

    [Fact]
    public async Task Fails_when_the_field_reporting_is_unavailable()
    {
        var harness = new Harness(LoginControls());
        harness.Ui.AcceptsTyping = false;
        harness.Ui.UsernameValue = null;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("did not receive the typed username", result.Message);
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
        Assert.Equal(2, harness.Mouse.Clicks.Count);
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
    public async Task Does_not_click_fields_without_a_screen_rectangle()
    {
        var harness = new Harness(LoginControls(username: new UiBounds(0, 0, 0, 0)));

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("cannot be clicked", result.Message);
        Assert.Empty(harness.Mouse.Clicks);
    }

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

    private sealed class Harness
    {
        public Harness(UiLoginControls? controls)
        {
            Ui = new FakeUi { Controls = controls };
            Keyboard = new RecordingKeyboard { Ui = Ui };
            Mouse = new RecordingMouse
            {
                // A click on a field focuses it, exactly like the real form does.
                OnClick = (x, y) => Keyboard.Focused = FocusedField(x, y),
            };
            Driver = new PointerLoginDriver(
                Ui,
                new StubProcess(),
                NullAppLog.Instance,
                Keyboard,
                Mouse,
                () => CanInject);
        }

        public FakeUi Ui { get; }

        public RecordingKeyboard Keyboard { get; }

        public RecordingMouse Mouse { get; }

        public bool CanInject { get; init; } = true;

        public PointerLoginDriver Driver { get; }

        /// <summary>Maps a click position back to the field it lands on (by vertical centre).</summary>
        private int FocusedField(int x, int y)
        {
            var controls = Ui.Controls;
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
                if (!bounds.IsEmpty && Math.Abs(bounds.CenterY - y) <= Math.Abs(bounds.Height / 2))
                {
                    return index;
                }
            }

            return -1;
        }
    }

    private sealed class FakeUi : IUiLoginUi
    {
        private readonly Dictionary<int, string> _values = [];

        public UiLoginControls? Controls { get; init; }

        public bool FormDisappears { get; set; } = true;

        /// <summary>False simulates a toolkit whose fields never see the synthetic keystrokes.</summary>
        public bool AcceptsTyping { get; set; } = true;

        /// <summary>Set to null to simulate a field whose value cannot be read.</summary>
        public string? UsernameValue { get; set; } = "";

        public List<(int Index, string Value)> SetCalls { get; } = [];

        public int WaitCalls { get; private set; }

        public UiLoginControls? FindLoginControls() => Controls;

        public string? GetFieldValue(int index) => index == 0 ? UsernameValue : _values.GetValueOrDefault(index);

        public bool SetField(int index, string value)
        {
            SetCalls.Add((index, value));
            _values[index] = value;
            return true;
        }

        public bool Submit() => false;

        public bool WaitUntilLoginControlsGone(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            WaitCalls++;
            return FormDisappears;
        }

        /// <summary>Called by the keyboard fake so the field reflects what was typed.</summary>
        public void Type(int index, string text)
        {
            if (!AcceptsTyping)
            {
                return;
            }

            _values[index] = (_values.GetValueOrDefault(index) ?? string.Empty) + text;
            if (index == 0)
            {
                UsernameValue = _values[0];
            }
        }

        public void Clear(int index)
        {
            _values[index] = string.Empty;
            if (index == 0)
            {
                UsernameValue = string.Empty;
            }
        }
    }

    /// <summary>Records injected keys and feeds them back into the fake UI's focused field.</summary>
    private sealed class RecordingKeyboard : IKeyboardInput
    {
        private int _focused = -1;

        public List<string> Events { get; } = [];

        public bool RejectText { get; set; }

        public int LastError => 0;

        public FakeUi? Ui { get; set; }

        public int Focused
        {
            set => _focused = value;
        }


        public bool SendText(string text)
        {
            if (RejectText)
            {
                return false;
            }

            Events.Add("text:" + text);
            if (_focused >= 0)
            {
                Ui?.Type(_focused, text);
            }

            return true;
        }

        public bool SendKeyPress(ushort key)
        {
            Events.Add("key:" + key);
            if (key == 0x08 && _focused >= 0)
            {
                Ui?.Clear(_focused);
            }

            return true;
        }

        public bool SendSelectAll()
        {
            Events.Add("selectall");
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

        public bool Focused { get; private set; }

        public Task StartAsync(AppInstallation installation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryFocusMainWindow()
        {
            Focused = true;
            return true;
        }

        public Task<bool> CloseMainWindowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task KillAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingMouse : IMouseInput
    {
        public List<(int X, int Y)> Clicks { get; } = [];

        public bool Reject { get; set; }

        public Action<int, int>? OnClick { get; set; }

        public int LastError => Reject ? 5 : 0;

        public bool Click(int x, int y)
        {
            if (Reject)
            {
                return false;
            }

            Clicks.Add((x, y));
            OnClick?.Invoke(x, y);
            return true;
        }
    }
}
