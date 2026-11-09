namespace IbkrManager.Platform.Windows;

/// <summary>
/// Preferred login driver: fills the Qt/QML login form through UI Automation and invokes its
/// "Log In" button. No focus stealing, no tab order, no keystroke timing — and it works even when
/// the session cannot deliver synthetic input (e.g. a non-interactive CI session).
/// Falls back to the next driver in the chain when no login form is exposed.
/// </summary>
public sealed class UiaLoginDriver(IUiLoginUi ui, IAppLog log) : ILoginDriver
{
    public string Name => "uia";

    public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var controls = ui.FindLoginControls();
        if (controls is null)
        {
            return Task.FromResult(LoginResult.DoesNotApply("no login form was exposed through UI Automation"));
        }

        log.Info($"UIA login: {controls.Description}");

        if (!ui.SetField(controls.UsernameIndex, request.Username.Reveal()))
        {
            return Task.FromResult(LoginResult.Failed("UI Automation could not set the username field"));
        }

        if (!ui.SetField(controls.PasswordIndex, request.Password.Reveal()))
        {
            return Task.FromResult(LoginResult.Failed("UI Automation could not set the password field"));
        }

        if (!controls.CanSubmit)
        {
            log.Warn("UIA login: the login button was not found; trying to submit anyway");
        }

        if (!ui.Submit())
        {
            return Task.FromResult(LoginResult.Failed("UI Automation could not submit the login form"));
        }

        if (!ui.WaitUntilLoginControlsGone(request.Profile.LoginResultTimeout, cancellationToken))
        {
            return Task.FromResult(LoginResult.Failed(
                "the login form is still showing after the credentials were submitted. " +
                "Check the credentials, or log in manually."));
        }

        return Task.FromResult(LoginResult.AwaitingTwoFactor(
            "Credentials submitted — enter the 2FA code in the app to finish logging in"));
    }
}
