namespace IbkrManager.Platform.Windows;

/// <summary>
/// Drives the login form like a person does: UI Automation is used only to *locate* the fields and
/// their screen rectangles, then the driver clicks each field and types into it.
///
/// This is the preferred driver for IBKR Desktop. It is a Qt/QML app whose text fields accept
/// <c>ValuePattern.SetValue</c> without updating the QML <c>text</c> property the form validates, so
/// assigning values makes the fields look filled while the app still reports "no username and
/// password". Clicks and keystrokes enter through Qt's normal input pipeline instead.
///
/// Two delivery strategies are tried in order, and the first one that visibly fills the form wins:
/// <list type="number">
/// <item><c>sendinput</c> — real mouse and keyboard events. Preferred, but Windows discards them in a
/// session without an input desktop and UIPI blocks them when the app runs elevated.</item>
/// <item><c>posted</c> — mouse and character messages posted to the app window. Works in service
/// sessions and across elevation because the app's own message loop delivers them.</item>
/// </list>
///
/// Every step is verified, because a form that merely *looks* filled is the failure mode that
/// produced this driver: the username is read back, the password field must report characters, and
/// the username must still be intact afterwards (proving focus moved instead of the password landing
/// in the username field). The password itself is never read back, only its length.
/// </summary>
public sealed class PointerLoginDriver(
    IUiLoginUi ui,
    IProcessController process,
    IAppLog log,
    IKeyboardInput? keyboard = null,
    IMouseInput? mouse = null,
    IPostedInput? postedInput = null,
    Func<bool>? isInteractive = null,
    IWindowInspector? inspector = null) : ILoginDriver
{
    /// <summary>Time for the window manager to settle after the window is raised.</summary>
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Time for the toolkit to move the caret after a click.</summary>
    private static readonly TimeSpan ClickDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Time for the form to process posted messages between steps.</summary>
    private static readonly TimeSpan PostedDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>Time for the form to re-enable its button after the last character landed.</summary>
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(400);

    private readonly IKeyboardInput _keyboard = keyboard ?? new SendInputKeyboard();
    private readonly IMouseInput _mouse = mouse ?? new SendInputMouse();
    private readonly IPostedInput _posted = postedInput ?? new PostedWindowInput(process);
    private readonly Func<bool> _isInteractive = isInteractive ?? InteractiveSession.IsInteractive;

    public string Name => "pointer";

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            log.Warn("Pointer login: could not raise the IBKR Desktop window; the app may not be focused");
        }

        await Task.Delay(FocusDelay, cancellationToken).ConfigureAwait(false);

        var username = request.Username.Reveal();
        var password = request.Password.Reveal();
        var typingDelay = request.Profile.TypingDelayMilliseconds;

        // Text that is already on the login screen (warnings, legal notices) must not be mistaken for
        // the app's answer to this login attempt.
        var preexistingTexts = DescribeAppTexts(username, password).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var strategies = Strategies();
        var problems = new List<string>();
        var used = (string?)null;
        foreach (var strategy in strategies)
        {
            var problem = await FillFieldsAsync(controls, username, password, typingDelay, strategy == "posted", cancellationToken)
                .ConfigureAwait(false);
            if (problem is null)
            {
                used = strategy;
                break;
            }

            problems.Add($"{strategy}: {problem}");
        }

        if (used is null)
        {
            log.Warn($"Pointer login: every input strategy failed: {string.Join(" | ", problems)}");
            return LoginResult.Failed(
                "could not type the credentials into the app. " + string.Join(" ", problems) +
                DescribeApp(username, password));
        }

        log.Info($"Pointer login: credentials typed with the {used} strategy");

        await Task.Delay(SubmitDelay, cancellationToken).ConfigureAwait(false);

        var usePosted = used == "posted";
        if (!SubmitLogin(controls, usePosted))
        {
            return LoginResult.Failed(
                controls.SubmitBounds.IsEmpty
                    ? "Windows rejected the Enter key"
                    : "Windows rejected the click on the login button");
        }

        log.Info("Pointer login: credentials submitted; waiting for the app to react");

        return await WaitForOutcomeAsync(request.Profile, username, password, preexistingTexts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a verdict instead of assuming the first screen change means success. IBKR Desktop
    /// replaces the login form with a "Connecting to server…" progress view while it talks to the
    /// backend, and a refused login brings the form back with a message — or shows a dialog. Reporting
    /// "awaiting 2FA" as soon as the form disappears would be wrong in both cases.
    /// </summary>
    private async Task<LoginResult> WaitForOutcomeAsync(
        AppProfile profile,
        string username,
        string password,
        HashSet<string> preexistingTexts,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + profile.LoginResultTimeout;
        var sawProgress = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = FindFailure(username, password, preexistingTexts);
            if (message is not null)
            {
                return LoginResult.Failed($"IBKR reported: \"{message}\"");
            }

            if (ui.FindLoginControls() is null && inspector?.Observe() is { } observation)
            {
                if (observation.Elements.Any(element => element.ControlType.Contains("Progress", StringComparison.OrdinalIgnoreCase)))
                {
                    // "Connecting to server…" — keep polling for the real answer.
                    sawProgress = true;
                }
                else
                {
                    var kind = UiClassifier.Classify(profile, observation.WindowTitle, observation);
                    if (kind == WindowKind.TwoFactor)
                    {
                        return LoginResult.AwaitingTwoFactor(
                            "Credentials accepted — enter the 2FA code in the app to finish logging in");
                    }

                    if (kind == WindowKind.LoggedIn && sawProgress)
                    {
                        return LoginResult.Submitted("IBKR accepted the credentials; the login screen is gone");
                    }
                }
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return LoginResult.Failed("Login cancelled");
            }
        }

        return ui.FindLoginControls() is not null
            ? LoginResult.Failed(
                "the login form is still showing after the credentials were typed. Check the credentials, " +
                "or log in manually." + DescribeApp(username, password))
            : LoginResult.Failed(
                $"IBKR did not answer within {profile.LoginResultTimeout.TotalSeconds:0} seconds; the app may still be " +
                "connecting. Check the app window." + DescribeApp(username, password));
    }

    /// <summary>Real input first on an interactive desktop; posted messages always remain available.</summary>
    private IEnumerable<string> Strategies()
    {
        if (_isInteractive())
        {
            yield return "sendinput";
        }

        yield return "posted";
    }

    /// <summary>Fills both fields with one strategy, verifying that each received its characters.</summary>
    private async Task<string?> FillFieldsAsync(
        UiLoginControls controls,
        string username,
        string password,
        int typingDelayMilliseconds,
        bool usePosted,
        CancellationToken cancellationToken)
    {
        var problem = await ClickAndTypeAsync(controls.UsernameIndex, controls.UsernameBounds, username, typingDelayMilliseconds, usePosted, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!string.Equals(ui.GetFieldValue(controls.UsernameIndex), username, StringComparison.Ordinal))
        {
            return "the app did not receive the typed username.";
        }

        // Password: Tab moves focus like a human would; fall back to clicking when that did not land.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 0)
            {
                if (!await MoveFocusToPasswordAsync(controls, password, typingDelayMilliseconds, usePosted, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return $"{Delivery(usePosted)} could not move focus to the password field.";
                }
            }
            else
            {
                problem = await ClickAndTypeAsync(controls.PasswordIndex, controls.PasswordBounds, password, typingDelayMilliseconds, usePosted, cancellationToken)
                    .ConfigureAwait(false);
                if (problem is not null)
                {
                    return problem;
                }
            }

            var usernameIntact = string.Equals(ui.GetFieldValue(controls.UsernameIndex), username, StringComparison.Ordinal);
            var passwordLength = ui.GetFieldLength(controls.PasswordIndex);
            if (usernameIntact && passwordLength is > 0)
            {
                return null;
            }

            log.Warn(
                $"Pointer login ({Delivery(usePosted)}): password attempt {attempt + 1} did not land " +
                $"(username intact: {usernameIntact}, password characters: {passwordLength?.ToString() ?? "unreadable"})");

            // A focus mishap can leave the password inside the username field; restore it before retrying.
            if (!usernameIntact)
            {
                problem = await ClickAndTypeAsync(controls.UsernameIndex, controls.UsernameBounds, username, typingDelayMilliseconds, usePosted, cancellationToken)
                    .ConfigureAwait(false);
                if (problem is not null)
                {
                    return problem;
                }

                if (!string.Equals(ui.GetFieldValue(controls.UsernameIndex), username, StringComparison.Ordinal))
                {
                    return "the app did not receive the typed username.";
                }
            }
        }

        return "the password field did not receive the typed characters.";
    }

    private async Task<bool> MoveFocusToPasswordAsync(
        UiLoginControls controls,
        string password,
        int typingDelayMilliseconds,
        bool usePosted,
        CancellationToken cancellationToken)
    {
        var moved = usePosted ? _posted.KeyPress(NativeMethods.VK_TAB) : _keyboard.SendKeyPress(NativeMethods.VK_TAB);
        if (!moved)
        {
            return false;
        }

        await Task.Delay(usePosted ? PostedDelay : ClickDelay, cancellationToken).ConfigureAwait(false);
        return await ClearAndTypeAsync(controls.PasswordIndex, password, typingDelayMilliseconds, usePosted, cancellationToken)
            .ConfigureAwait(false) is null;
    }

    /// <summary>Clicks the field, replaces its contents and types the value.</summary>
    private async Task<string?> ClickAndTypeAsync(
        int index,
        UiBounds bounds,
        string value,
        int typingDelayMilliseconds,
        bool usePosted,
        CancellationToken cancellationToken)
    {
        var clicked = usePosted
            ? _posted.Click(bounds.CenterX, bounds.CenterY)
            : _mouse.Click(bounds.CenterX, bounds.CenterY);
        if (!clicked)
        {
            return $"Windows rejected the simulated click ({Delivery(usePosted)}).";
        }

        await Task.Delay(usePosted ? PostedDelay : ClickDelay, cancellationToken).ConfigureAwait(false);
        return await ClearAndTypeAsync(index, value, typingDelayMilliseconds, usePosted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the focused field's contents with <paramref name="value"/>.</summary>
    private async Task<string?> ClearAndTypeAsync(
        int index,
        string value,
        int typingDelayMilliseconds,
        bool usePosted,
        CancellationToken cancellationToken)
    {
        if (usePosted)
        {
            // Posted messages cannot carry modifiers, so clear with one Backspace per character.
            var existing = Math.Clamp(ui.GetFieldLength(index) ?? 0, 0, 200);
            for (var i = 0; i < existing; i++)
            {
                if (!_posted.KeyPress(NativeMethods.VK_BACK))
                {
                    return "Windows rejected the posted Backspace.";
                }
            }
        }
        else if (!_keyboard.SendSelectAll() || !_keyboard.SendKeyPress(NativeMethods.VK_BACK))
        {
            return $"Windows rejected the simulated keyboard input (SendInput error {_keyboard.LastError})";
        }

        foreach (var character in value)
        {
            var sent = usePosted ? _posted.Text(character) : _keyboard.SendText(character.ToString());
            if (!sent)
            {
                return usePosted
                    ? "Windows rejected the posted character."
                    : $"Windows rejected the simulated keyboard input (SendInput error {_keyboard.LastError})";
            }

            if (typingDelayMilliseconds > 0)
            {
                await Task.Delay(typingDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private bool SubmitLogin(UiLoginControls controls, bool usePosted)
    {
        if (controls.SubmitBounds.IsEmpty)
        {
            return usePosted
                ? _posted.KeyPress(NativeMethods.VK_RETURN)
                : _keyboard.SendKeyPress(NativeMethods.VK_RETURN);
        }

        return usePosted
            ? _posted.Click(controls.SubmitBounds.CenterX, controls.SubmitBounds.CenterY)
            : _mouse.Click(controls.SubmitBounds.CenterX, controls.SubmitBounds.CenterY);
    }

    private static string Delivery(bool usePosted) => usePosted ? "posted messages" : "SendInput";

    private static readonly System.Text.RegularExpressions.Regex FailureText = new(
        @"invalid|incorrect|failed|failure|wrong|not\s+(recognised|recognized|valid|found)|denied|unsuccessful|try\s+again|could\s+not|unable\s+to|timed\s+out|error",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The app's own answer to this login attempt, or null when it has not answered yet.</summary>
    private string? FindFailure(string username, string password, HashSet<string> preexistingTexts) =>
        DescribeAppTexts(username, password)
            .FirstOrDefault(text => !preexistingTexts.Contains(text) && FailureText.IsMatch(text));

    /// <summary>
    /// The app's own visible text, so a failure explains itself ("Please enter your username and
    /// password" vs "Invalid username or password"). Credentials are filtered out.
    /// </summary>
    private string DescribeApp(params string[] secrets)
    {
        var texts = DescribeAppTexts(secrets).Take(6).ToList();
        return texts.Count == 0 ? string.Empty : " The app shows: " + string.Join(" / ", texts.Select(text => $"\"{text}\""));
    }

    private List<string> DescribeAppTexts(params string[] secrets)
    {
        var observation = inspector?.Observe();
        if (observation is null)
        {
            return [];
        }

        return observation.Elements
            .Select(element => element.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.Length is > 2 and < 200)
            .Where(name => !secrets.Any(secret => !string.IsNullOrEmpty(secret) && name!.Contains(secret, StringComparison.OrdinalIgnoreCase)))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
