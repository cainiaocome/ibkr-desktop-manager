using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

public sealed class UiaLoginDriverTests
{
    private static readonly AppProfile Profile = AppProfile.Default with
    {
        LoginResultTimeout = TimeSpan.Zero,
        LoginAutomationIdPatterns = ["launcherLoginWindow"],
        LoginButtonNames = ["Log In"],
    };

    [Fact]
    public async Task Does_not_apply_when_no_login_form_is_exposed()
    {
        var harness = new Harness(controls: null);

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.NotApplicable);
        Assert.False(result.Success);
        Assert.Empty(harness.Ui.SetCalls);
    }

    [Fact]
    public async Task Sets_username_then_password_then_submits()
    {
        var harness = new Harness(new UiLoginControls(0, 1, CanSubmit: true, "two fields"));

        var result = await harness.Driver.LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor);
        Assert.Equal([(0, "alice"), (1, "secret")], harness.Ui.SetCalls);
        Assert.Equal(1, harness.Ui.SubmitCalls);
        Assert.Equal(1, harness.Ui.WaitCalls);
        Assert.Contains("2FA", result.Message);
    }

    [Fact]
    public async Task Honours_the_indexes_reported_by_the_automation_session()
    {
        // Password first: the driver must not assume field order.
        var harness = new Harness(new UiLoginControls(1, 0, CanSubmit: false, "reversed"));

        _ = await harness.Driver.LoginAsync(Request());

        Assert.Equal([(1, "alice"), (0, "secret")], harness.Ui.SetCalls);
    }

    [Fact]
    public async Task Fails_without_submitting_when_the_password_cannot_be_set()
    {
        var harness = new Harness(new UiLoginControls(0, 1, CanSubmit: true, "two fields"));
        harness.Ui.FailField = 1;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("password", result.Message);
        Assert.Equal(0, harness.Ui.SubmitCalls);
    }

    [Fact]
    public async Task Fails_when_the_form_does_not_go_away()
    {
        var harness = new Harness(new UiLoginControls(0, 1, CanSubmit: true, "two fields"));
        harness.Ui.FormDisappears = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("still showing", result.Message);
        Assert.Equal(1, harness.Ui.SubmitCalls);
    }

    [Fact]
    public async Task Fails_when_the_submit_button_is_missing_and_enter_is_refused()
    {
        var harness = new Harness(new UiLoginControls(0, 1, CanSubmit: false, "no button"));
        harness.Ui.SubmitResult = false;

        var result = await harness.Driver.LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("submit", result.Message);
    }

    private static LoginRequest Request() => new(new Secret("alice"), new Secret("secret"), Profile);

    private sealed class Harness
    {
        public Harness(UiLoginControls? controls)
        {
            Ui = new FakeUi { Controls = controls };
            Driver = new UiaLoginDriver(Ui, NullAppLog.Instance);
        }

        public FakeUi Ui { get; }

        public UiaLoginDriver Driver { get; }
    }

    private sealed class FakeUi : IUiLoginUi
    {
        public UiLoginControls? Controls { get; set; }

        public int? FailField { get; set; }

        public bool FormDisappears { get; set; } = true;

        public bool SubmitResult { get; set; } = true;

        public List<(int Index, string Value)> SetCalls { get; } = [];

        public int SubmitCalls { get; private set; }

        public int WaitCalls { get; private set; }

        public UiLoginControls? FindLoginControls() => Controls;

        public string? GetFieldValue(int index) => null;

        public bool SetField(int index, string value)
        {
            if (FailField == index)
            {
                return false;
            }

            SetCalls.Add((index, value));
            return true;
        }

        public bool Submit()
        {
            SubmitCalls++;
            return SubmitResult;
        }

        public bool WaitUntilLoginControlsGone(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            WaitCalls++;
            return FormDisappears;
        }
    }
}
