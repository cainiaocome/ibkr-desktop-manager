namespace IbkrManager.Platform.Windows;

/// <summary>
/// Types the credentials into whatever field currently has focus, using Tab to move between
/// fields (username → password → submit). Works without UI Automation, which is the only option
/// IBKR Desktop's Java UI reliably exposes; it is also the part most likely to need tuning after
/// the real-app spike, hence the profile-driven timings.
/// </summary>
public sealed class SendInputLoginDriver(
    IProcessController process,
    IClock clock,
    IAppLog log) : ILoginDriver
{
    public string Name => "sendinput";

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!process.TryFocusMainWindow())
        {
            log.Warn("Login: could not focus the IBKR Desktop window");
        }

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        // Clear anything pre-filled in the username field, then type.
        Keyboard.SendSelectAll();
        Keyboard.SendKeyPress(NativeMethods.VK_BACK);
        if (!await TypeAsync(request.Username.Reveal(), request.Profile.TypingDelayMilliseconds, cancellationToken).ConfigureAwait(false))
        {
            return LoginResult.Failed("Windows rejected the simulated keystrokes");
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        if (!Keyboard.SendKeyPress(NativeMethods.VK_TAB))
        {
            return LoginResult.Failed("Could not move focus to the password field");
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        if (!await TypeAsync(request.Password.Reveal(), request.Profile.TypingDelayMilliseconds, cancellationToken).ConfigureAwait(false))
        {
            return LoginResult.Failed("Windows rejected the simulated keystrokes");
        }

        log.Info($"Typed credentials for {request.Profile.DisplayName}; waiting for the app to react");

        if (request.Profile.SubmitAfterPassword)
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            Keyboard.SendKeyPress(NativeMethods.VK_RETURN);
        }

        return await WaitForOutcomeAsync(request.Profile, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TypeAsync(string value, int delayMilliseconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (delayMilliseconds <= 0)
        {
            return Keyboard.SendText(value);
        }

        foreach (var character in value)
        {
            if (!Keyboard.SendText(character.ToString()))
            {
                return false;
            }

            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<LoginResult> WaitForOutcomeAsync(AppProfile profile, CancellationToken cancellationToken)
    {
        var deadline = clock.UtcNow + profile.LoginResultTimeout;
        while (clock.UtcNow < deadline)
        {
            switch (profile.Classify(process.MainWindowTitle))
            {
                case WindowKind.TwoFactor:
                    return LoginResult.AwaitingTwoFactor(
                        "Credentials submitted — enter the 2FA code in the app to finish logging in");
                case WindowKind.LoggedIn:
                    return LoginResult.Submitted("Logged in");
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

        return LoginResult.Failed(
            "the login window is still showing after typing the credentials. Check the credentials, or log in manually.");
    }
}
