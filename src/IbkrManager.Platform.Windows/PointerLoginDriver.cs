namespace IbkrManager.Platform.Windows;

/// <summary>
/// Drives the login form like a person does: UI Automation is used only to *locate* the fields and
/// their screen rectangles, then the driver clicks each field with the mouse and types the value with
/// real keystrokes.
///
/// This is the preferred driver for IBKR Desktop. It is a Qt/QML app whose text fields accept
/// <c>ValuePattern.SetValue</c> without updating the QML <c>text</c> property the form validates, so
/// assigning values makes the fields look filled while the app still reports "no username and
/// password". Synthetic clicks and keystrokes enter through Qt's normal input pipeline and therefore
/// behave exactly like a human typing.
///
/// Every step is verified, because a form that merely *looks* filled is the failure mode that
/// produced this driver:
/// <list type="bullet">
/// <item>the username is read back and must match what was typed;</item>
/// <item>after typing the password, the username must still be intact (proving focus moved instead of
/// the password being appended to the username field) and the password field must report at least
/// one character;</item>
/// <item>the password is never read back, only its length.</item>
/// </list>
/// When the form is still on screen afterwards, the app's own message is included in the error.
/// </summary>
public sealed class PointerLoginDriver(
    IUiLoginUi ui,
    IProcessController process,
    IAppLog log,
    IKeyboardInput? keyboard = null,
    IMouseInput? mouse = null,
    Func<bool>? canInject = null,
    IWindowInspector? inspector = null) : ILoginDriver
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
        var password = request.Password.Reveal();
        var typingDelay = request.Profile.TypingDelayMilliseconds;

        var problem = await FillFieldsAsync(controls, username, password, typingDelay, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return LoginResult.Failed(problem + DescribeApp(username, password));
        }

        await Task.Delay(SubmitDelay, cancellationToken).ConfigureAwait(false);

        if (!SubmitLogin(controls))
        {
            return LoginResult.Failed(
                controls.SubmitBounds.IsEmpty
                    ? $"Windows rejected the Enter key (SendInput error {_keyboard.LastError})"
                    : $"Windows rejected the simulated click on the login button (mouse error {_mouse.LastError})");
        }

        log.Info("Pointer login: credentials typed and submitted; waiting for the app to react");

        if (ui.WaitUntilLoginControlsGone(request.Profile.LoginResultTimeout, cancellationToken))
        {
            return LoginResult.AwaitingTwoFactor(
                "Credentials submitted — enter the 2FA code in the app to finish logging in");
        }

        return LoginResult.Failed(
            "the login form is still showing after the credentials were typed. " +
            "Check the credentials, or log in manually." + DescribeApp(username, password));
    }

    /// <summary>Fills both fields, verifying that each one actually received its characters.</summary>
    private async Task<string?> FillFieldsAsync(
        UiLoginControls controls,
        string username,
        string password,
        int typingDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        // Username: click, replace the contents, type.
        var problem = await ClickAndTypeAsync(controls.UsernameBounds, username, typingDelayMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!string.Equals(ui.GetFieldValue(controls.UsernameIndex), username, StringComparison.Ordinal))
        {
            log.Warn($"Pointer login: the username field reports {ui.GetFieldLength(controls.UsernameIndex)?.ToString() ?? "unreadable"} character(s) after typing {username.Length}");
            return "the app did not receive the typed username, so simulated keyboard input is not reaching it. " +
                   "Make sure the IBKR Desktop window is visible, not covered by a UAC prompt, and running at the " +
                   "same privilege level as this manager.";
        }

        // Password: Tab moves focus like a human would; fall back to clicking if that did not land.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 0)
            {
                if (!_keyboard.SendKeyPress(NativeMethods.VK_TAB))
                {
                    log.Warn("Pointer login: Tab was rejected; clicking the password field instead");
                    problem = await ClickPasswordAsync(controls, password, typingDelayMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(ClickDelay, cancellationToken).ConfigureAwait(false);
                    problem = await ClearAndTypeAsync(password, typingDelayMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                problem = await ClickPasswordAsync(controls, password, typingDelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (problem is not null)
            {
                return problem;
            }

            var usernameIntact = string.Equals(ui.GetFieldValue(controls.UsernameIndex), username, StringComparison.Ordinal);
            var passwordLength = ui.GetFieldLength(controls.PasswordIndex);
            if (usernameIntact && passwordLength is > 0)
            {
                return null;
            }

            log.Warn(
                $"Pointer login: password attempt {attempt + 1} did not land " +
                $"(username intact: {usernameIntact}, password characters: {passwordLength?.ToString() ?? "unreadable"})");

            // A focus mishap can leave the password inside the username field; restore it before retrying.
            if (!usernameIntact)
            {
                problem = await ClickAndTypeAsync(controls.UsernameBounds, username, typingDelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
                if (problem is not null)
                {
                    return problem;
                }
            }
        }

        return "the password field did not receive the typed characters, so simulated keyboard input is not reaching it. " +
               "Make sure the IBKR Desktop window is focused and running at the same privilege level as this manager.";
    }

    private async Task<string?> ClickPasswordAsync(
        UiLoginControls controls,
        string password,
        int typingDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!_mouse.Click(controls.PasswordBounds.CenterX, controls.PasswordBounds.CenterY))
        {
            log.Warn($"Pointer login: click on the password field failed (error {_mouse.LastError})");
            return $"Windows rejected the simulated click on the password field (mouse error {_mouse.LastError})";
        }

        await Task.Delay(ClickDelay, cancellationToken).ConfigureAwait(false);
        return await ClearAndTypeAsync(password, typingDelayMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ClickAndTypeAsync(
        UiBounds bounds,
        string value,
        int typingDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!_mouse.Click(bounds.CenterX, bounds.CenterY))
        {
            log.Warn($"Pointer login: click on the login field failed (error {_mouse.LastError})");
            return $"Windows rejected the simulated click on the login field (mouse error {_mouse.LastError})";
        }

        await Task.Delay(ClickDelay, cancellationToken).ConfigureAwait(false);
        return await ClearAndTypeAsync(value, typingDelayMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the focused field's contents with <paramref name="value"/>.</summary>
    private async Task<string?> ClearAndTypeAsync(string value, int typingDelayMilliseconds, CancellationToken cancellationToken)
    {
        if (!_keyboard.SendSelectAll() || !_keyboard.SendKeyPress(NativeMethods.VK_BACK))
        {
            return $"Windows rejected the simulated keyboard input (SendInput error {_keyboard.LastError})";
        }

        foreach (var character in value)
        {
            if (!_keyboard.SendText(character.ToString()))
            {
                return $"Windows rejected the simulated keyboard input (SendInput error {_keyboard.LastError})";
            }

            if (typingDelayMilliseconds > 0)
            {
                await Task.Delay(typingDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private bool SubmitLogin(UiLoginControls controls) =>
        controls.SubmitBounds.IsEmpty
            ? _keyboard.SendKeyPress(NativeMethods.VK_RETURN)
            : _mouse.Click(controls.SubmitBounds.CenterX, controls.SubmitBounds.CenterY);

    /// <summary>
    /// The app's own visible text, so a failure explains itself ("Please enter your username and
    /// password" vs "Invalid username or password"). Credentials are filtered out.
    /// </summary>
    private string DescribeApp(params string[] secrets)
    {
        var observation = inspector?.Observe();
        if (observation is null)
        {
            return string.Empty;
        }

        var texts = observation.Elements
            .Select(element => element.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.Length is > 2 and < 200)
            .Where(name => !secrets.Any(secret => !string.IsNullOrEmpty(secret) && name!.Contains(secret, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        return texts.Count == 0 ? string.Empty : " The app shows: " + string.Join(" / ", texts.Select(text => $"\"{text}\""));
    }
}
