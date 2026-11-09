namespace IbkrManager.Platform.Windows;

/// <summary>
/// Drives the login form like a person does: UI Automation is used only to *locate* the fields and
/// their screen rectangles, then the driver clicks each field with the mouse and types the value with
/// real keystrokes.
///
/// This is the preferred driver for IBKR Desktop. It is a Qt/QML app whose text fields accept
/// <c>ValuePattern.SetValue</c> without updating the QML <c>text</c> property the form validates, so
/// setting values makes the fields look filled while the app still reports "no username and
/// password". Synthetic clicks and keystrokes enter through Qt's normal input pipeline and therefore
/// behave exactly like a human typing.
///
/// The username field is read back after typing: a mismatch means the input never reached the app
/// (no input desktop, window not focused), and the login aborts instead of submitting an empty form.
/// </summary>
public sealed class PointerLoginDriver(
    IUiLoginUi ui,
    IProcessController process,
    IAppLog log,
    IKeyboardInput? keyboard = null,
    IMouseInput? mouse = null,
    Func<bool>? canInject = null) : ILoginDriver
{
    /// <summary>Time for the window manager to settle after the window is raised.</summary>
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Time for the toolkit to move the caret after a click.</summary>
    private static readonly TimeSpan ClickDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Time for the form to re-enable its button after the last character landed.</summary>
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(400);

    private readonly IKeyboardInput _keyboard = keyboard ?? new SendInputKeyboard();
    private readonly IMouseInput _mouse = mouse ?? new SendInputMouse();
    private readonly Func<bool> _canInject = canInject ?? InteractiveSession.IsInteractive;

    public string Name => "pointer";

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_canInject())
        {
            return LoginResult.DoesNotApply(
                $"this session cannot deliver synthetic mouse and keyboard input ({InteractiveSession.Describe()})");
        }

        var controls = ui.FindLoginControls();
        if (controls is null)
        {
            return LoginResult.DoesNotApply("no login form was exposed through UI Automation");
        }

        log.Info($"Pointer login: {controls}");

        if (controls.UsernameBounds.IsEmpty || controls.PasswordBounds.IsEmpty)
        {
            return LoginResult.Failed(
                "UI Automation did not report where the login fields are, so they cannot be clicked");
        }

        if (!process.TryFocusMainWindow())
        {
            log.Warn("Pointer login: could not raise the IBKR Desktop window; typing into whatever has focus");
        }

        await Task.Delay(FocusDelay, cancellationToken).ConfigureAwait(false);

        var username = request.Username.Reveal();
        var typingDelay = request.Profile.TypingDelayMilliseconds;
        var problem = await FillFieldAsync(controls.UsernameIndex, controls.UsernameBounds, username, typingDelay, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return LoginResult.Failed(problem);
        }

        var readBack = ui.GetFieldValue(controls.UsernameIndex);
        if (!string.Equals(readBack, username, StringComparison.Ordinal))
        {
            log.Warn($"Pointer login: the username field reports {readBack?.Length ?? 0} character(s) after typing {username.Length}");
            return LoginResult.Failed(
                "the app did not receive the typed username, so simulated input is not reaching it. " +
                "Make sure the IBKR Desktop window is visible and not blocked by a UAC/elevation prompt, then try again.");
        }

        problem = await FillFieldAsync(controls.PasswordIndex, controls.PasswordBounds, request.Password.Reveal(), typingDelay, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return LoginResult.Failed(problem);
        }

        await Task.Delay(SubmitDelay, cancellationToken).ConfigureAwait(false);

        if (!SubmitLogin(controls))
        {
            return LoginResult.Failed(
                controls.SubmitBounds.IsEmpty
                    ? $"Windows rejected the Enter key (SendInput error {_keyboard.LastError})"
                    : $"Windows rejected the simulated click on the login button (SendInput error {_mouse.LastError})");
        }

        log.Info("Pointer login: credentials typed and submitted; waiting for the app to react");

        return ui.WaitUntilLoginControlsGone(request.Profile.LoginResultTimeout, cancellationToken)
            ? LoginResult.AwaitingTwoFactor("Credentials submitted — enter the 2FA code in the app to finish logging in")
            : LoginResult.Failed(
                "the login form is still showing after the credentials were typed. " +
                "Check the credentials, or log in manually.");
    }

    /// <summary>Clicks the field, replaces its contents and types the value; returns an error or null.</summary>
    private async Task<string?> FillFieldAsync(
        int index,
        UiBounds bounds,
        string value,
        int typingDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!_mouse.Click(bounds.CenterX, bounds.CenterY))
        {
            log.Warn($"Pointer login: click on field {index} failed (error {_mouse.LastError})");
            return $"Windows rejected the simulated click on the login field (error {_mouse.LastError})";
        }

        await Task.Delay(ClickDelay, cancellationToken).ConfigureAwait(false);

        if (!_keyboard.SendSelectAll() || !_keyboard.SendKeyPress(NativeMethods.VK_BACK))
        {
            return $"Windows rejected the simulated keyboard input (SendInput error {_keyboard.LastError})";
        }

        return await TypeAsync(value, typingDelayMilliseconds, cancellationToken).ConfigureAwait(false)
            ? null
            : $"Windows rejected the simulated keyboard input (SendInput error {_keyboard.LastError})";
    }

    private async Task<bool> TypeAsync(string value, int typingDelayMilliseconds, CancellationToken cancellationToken)
    {
        foreach (var character in value)
        {
            if (!_keyboard.SendText(character.ToString()))
            {
                return false;
            }

            if (typingDelayMilliseconds > 0)
            {
                await Task.Delay(typingDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    private bool SubmitLogin(UiLoginControls controls) =>
        controls.SubmitBounds.IsEmpty
            ? _keyboard.SendKeyPress(NativeMethods.VK_RETURN)
            : _mouse.Click(controls.SubmitBounds.CenterX, controls.SubmitBounds.CenterY);
}
