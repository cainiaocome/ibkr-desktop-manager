using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class UiAutomationTests
{
    private static readonly AppProfile Profile = AppProfile.Default with
    {
        LoginWindowTitlePatterns = ["Login"],
        LoggedInWindowTitlePatterns = ["IBKR Desktop"],
        LoginAutomationIdPatterns = ["launcherLoginWindow"],
        LoginButtonNames = ["Log In"],
    };

    private static UiObservation LoginForm => new("IBKR Desktop",
    [
        new UiElementInfo("Window", "IBKR Desktop", "QApplication.launcherLoginWindow", false),
        new UiElementInfo("Edit", "", "QApplication.launcherLoginWindow.TextField_QMLTYPE_74", false),
        new UiElementInfo("Edit", "", "QApplication.launcherLoginWindow.TextField_QMLTYPE_74", true),
        new UiElementInfo("Button", "Log In", "QApplication.launcherLoginWindow.NgStyleButtonBase", false),
    ]);

    [Fact]
    public void Login_form_is_recognised_even_though_the_title_looks_logged_in()
    {
        // The title alone would classify as LoggedIn ("IBKR Desktop" matches the logged-in patterns).
        Assert.Equal(WindowKind.LoggedIn, Profile.Classify("IBKR Desktop"));
        Assert.Equal(WindowKind.Login, UiClassifier.Classify(Profile, "IBKR Desktop", LoginForm));
    }

    [Fact]
    public void Login_button_name_alone_is_enough()
    {
        var observation = new UiObservation("IBKR Desktop",
        [
            new UiElementInfo("Button", "Log In", "some.other.id", false),
        ]);

        Assert.Equal(WindowKind.Login, UiClassifier.Classify(Profile, "IBKR Desktop", observation));
    }

    [Fact]
    public void Two_factor_markers_win_over_login_markers()
    {
        var profile = Profile with { TwoFactorAutomationIdPatterns = ["SecurityCode"] };
        var observation = new UiObservation("IBKR Desktop",
        [
            new UiElementInfo("Window", "IBKR Desktop", "QApplication.launcherLoginWindow", false),
            new UiElementInfo("Edit", "", "QApplication.securityCode.TextField", false),
        ]);

        Assert.Equal(WindowKind.TwoFactor, UiClassifier.Classify(profile, "IBKR Desktop", observation));
    }

    [Fact]
    public void Snapshot_without_login_markers_means_past_the_login_screen()
    {
        var observation = new UiObservation("IBKR Desktop",
        [
            new UiElementInfo("Window", "IBKR Desktop", "QApplication.mainWindow", false),
        ]);

        Assert.Equal(WindowKind.LoggedIn, UiClassifier.Classify(Profile, "IBKR Desktop", observation));
    }

    [Fact]
    public void Empty_observation_falls_back_to_the_title()
    {
        var observation = new UiObservation("IBKR Desktop", []);

        Assert.Equal(WindowKind.LoggedIn, UiClassifier.Classify(Profile, "IBKR Desktop", observation));
        Assert.Equal(WindowKind.Login, UiClassifier.Classify(Profile, "IBKR Desktop - Login", observation));
    }

    [Fact]
    public void Missing_observation_falls_back_to_the_title()
    {
        Assert.Equal(WindowKind.Unknown, UiClassifier.Classify(Profile, "Something else", null));
    }

    [Fact]
    public void Null_inspector_never_throws()
    {
        Assert.Null(NullWindowInspector.Instance.Observe());
    }

    [Fact]
    public void Observation_helpers_count_and_describe()
    {
        Assert.Equal(2, LoginForm.CountOf("Edit"));
        Assert.True(LoginForm.HasButtonNamed(["log in"]));
        Assert.False(LoginForm.HasButtonNamed(["Sign In"]));
        Assert.Contains("Edit", LoginForm.Describe());
    }
}

public sealed class LoginDriverChainTests
{
    [Fact]
    public async Task Falls_through_to_the_next_driver()
    {
        var first = new FakeDriver("uia", LoginResult.DoesNotApply("no form"));
        var second = new FakeDriver("sendinput", LoginResult.Submitted("typed"));
        var chain = new LoginDriverChain(first, second);

        var result = await chain.LoginAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal("uia -> sendinput", chain.Name);
    }

    [Fact]
    public async Task Stops_on_success()
    {
        var first = new FakeDriver("uia", LoginResult.AwaitingTwoFactor("2fa"));
        var second = new FakeDriver("sendinput", LoginResult.Submitted("typed"));

        var result = await new LoginDriverChain(first, second).LoginAsync(Request());

        Assert.True(result.ReachedTwoFactor);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Stops_on_failure_instead_of_retrying_credentials()
    {
        var first = new FakeDriver("uia", LoginResult.Failed("wrong password"));
        var second = new FakeDriver("sendinput", LoginResult.Submitted("typed"));

        var result = await new LoginDriverChain(first, second).LoginAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Reports_the_last_reason_when_nothing_applies()
    {
        var chain = new LoginDriverChain(
            new FakeDriver("uia", LoginResult.DoesNotApply("no form")),
            new FakeDriver("sendinput", LoginResult.DoesNotApply("no window")));

        var result = await chain.LoginAsync(Request());

        Assert.True(result.NotApplicable);
        Assert.Contains("no window", result.Message);
    }

    private static LoginRequest Request() => new(new Secret("user"), new Secret("pass"), AppProfile.Default);

    private sealed class FakeDriver(string name, LoginResult result) : ILoginDriver
    {
        public string Name => name;

        public int Calls { get; private set; }

        public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
