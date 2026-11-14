namespace IbkrManager.Core;

/// <summary>
/// Orchestrates locate → start → login → quit and derives the state the UI renders.
/// All OS access goes through injected interfaces, so this type is fully unit-testable.
/// </summary>
public sealed class AppManager : IAppManager, IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IAppLocator _locator;
    private readonly IProcessController _process;
    private readonly IAccountService _accounts;
    private readonly ILoginDriver _loginDriver;
    private readonly IClock _clock;
    private readonly IAppLog _log;
    private readonly ICloseConfirmationPrompt _closeConfirmation;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppInstallation? _installation;
    private string? _lastError;
    private string? _lastAction;
    private bool _failed;
    private bool _busy;
    private bool _disposed;
    private bool _stopping;
    private LoginAttempt? _lastAttempt;
    private int _accountsVersion = -1;
    private IReadOnlyList<AccountSummary> _accountSummaries = [];
    private ResolvedCredentials _resolved = ResolvedCredentials.Empty;
    private AppState _state = AppState.Initial;

    public AppManager(
        IAppLocator locator,
        IProcessController process,
        IAccountService accounts,
        ILoginDriver loginDriver,
        AppProfile profile,
        IClock clock,
        IAppLog log,
        TimeSpan? pollInterval = null,
        ICloseConfirmationPrompt? closeConfirmation = null)
    {
        _locator = locator;
        _process = process;
        _accounts = accounts;
        _loginDriver = loginDriver;
        Profile = profile;
        _clock = clock;
        _log = log;
        _closeConfirmation = closeConfirmation ?? NullCloseConfirmationPrompt.Instance;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public AppState State => _state;

    public AppProfile Profile { get; }

    public event EventHandler<AppState>? StateChanged;

    public async Task<AppState> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _installation = _locator.Locate();
        if (_installation is null)
        {
            _log.Info($"{Profile.DisplayName} was not found on this machine");
        }
        else
        {
            _log.Info($"Found {Profile.DisplayName} at {_installation}");
        }

        return await TickAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppState> TickAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return _state;
        }

        // Never block the polling timer behind a long-running command.
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return _state;
        }

        AppState state;
        try
        {
            state = BuildState();
        }
        finally
        {
            _gate.Release();
        }

        Raise(state);
        return state;
    }

    public Task<AppState> StartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("Start", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;

            EnsureInstalled();
            if (_process.IsRunning)
            {
                _lastAction = "Already running — attached to the existing process";
                _log.Info("IBKR Desktop is already running; attached instead of launching a second copy");
                return;
            }

            await LaunchAsync(ct).ConfigureAwait(false);
        });

    public Task<AppState> AutoLoginAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("Auto login", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;

            var credentials = _accounts.Resolve();
            if (credentials.AccountId is null)
            {
                throw new AppManagerException(
                    "Auto Login blocked: no account is selected. Add one in Manage accounts.");
            }

            if (!credentials.IsComplete)
            {
                var hint = !credentials.HasUsername
                    ? credentials.UsernameHint ?? "username is not set"
                    : credentials.PasswordHint ?? "password is not set";
                throw new AppManagerException(
                    $"Auto Login blocked: account {credentials.AccountDescription} — {hint}.");
            }

            EnsureInstalled();

            if (!_process.IsRunning)
            {
                await LaunchAsync(ct).ConfigureAwait(false);
            }

            if (!_process.HasVisibleWindow)
            {
                throw new AppManagerException(
                    "Auto Login failed: the IBKR Desktop window did not appear. Try Start, then wait for the login screen.");
            }

            if (!_process.TryFocusMainWindow())
            {
                _log.Warn("Could not bring the IBKR Desktop window to the foreground; typing may have gone elsewhere");
            }

            _lastAction = $"Typing credentials ({credentials.SourcesDescription})";
            var result = await _loginDriver.LoginAsync(
                new LoginRequest(credentials.Username!, credentials.Password!, Profile),
                ct).ConfigureAwait(false);

            // The attempt is recorded as an outcome, not as live state: the manager does not watch the
            // app afterwards, so whatever happens next (2FA, rejection, success) is the user's to see.
            _lastAttempt = new LoginAttempt(_clock.UtcNow, result.Success, result.ReachedTwoFactor, result.Message);

            if (result.NotApplicable)
            {
                throw new AppManagerException(
                    "Auto login failed: the login screen was not exposed. Bring IBKR Desktop to its login " +
                    "form and try again.");
            }

            if (!result.Success)
            {
                throw new AppManagerException($"Auto login failed: {result.Message}");
            }

            _lastAction = result.Message;
        });

    public Task<AppState> QuitAsync(bool force = false, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Quit", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;
            _stopping = true;

            if (!_process.IsRunning)
            {
                _lastAction = "Already stopped";
                return;
            }

            if (force)
            {
                await _process.KillAsync(ct).ConfigureAwait(false);
                _stopping = false;
                _lastAction = "Terminated the process tree";
                _log.Warn("Force-quit IBKR Desktop");
                return;
            }

            var outcome = await TryCloseAsync(ct).ConfigureAwait(false);
            _stopping = false;
            _lastAction = outcome.Message;

            if (outcome.Exited)
            {
                _log.Info(outcome.Message);
                return;
            }

            // Deliberately no escalation to TerminateProcess: the app may still be flushing state, and
            // killing it is what the user asked us to stop doing. Force Quit is the explicit way out.
            _log.Warn($"{outcome.Message}; the process was left running (use Force Quit to end it)");
        });

    /// <summary>
    /// Asks IBKR Desktop to close, answers its confirmation if it shows one, and reports the outcome.
    /// Never terminates the process.
    /// </summary>
    private async Task<CloseOutcome> TryCloseAsync(CancellationToken cancellationToken)
    {
        // Snapshot first: a confirmation can only be a window that appears afterwards.
        var mainWindow = _process.MainWindowHandle ?? IntPtr.Zero;
        var windowsBefore = _process.FindWindows();
        var requested = await _process.CloseMainWindowAsync(cancellationToken).ConfigureAwait(false);
        if (!requested)
        {
            return new CloseOutcome(false, CloseConfirmation.NotShown, "The close request could not be delivered");
        }

        if (await _process.WaitForExitAsync(Profile.GracefulShutdownTimeout, cancellationToken).ConfigureAwait(false))
        {
            return new CloseOutcome(true, CloseConfirmation.NotShown, "Closed gracefully");
        }

        // Still there: the app is most likely asking "really quit?".
        var confirmation = await _closeConfirmation
            .AnswerAsync(new CloseConfirmationRequest(mainWindow, windowsBefore), cancellationToken)
            .ConfigureAwait(false);
        if (confirmation == CloseConfirmation.Answered)
        {
            _log.Info("Answered IBKR Desktop's quit confirmation");
            if (await _process.WaitForExitAsync(Profile.GracefulShutdownTimeout, cancellationToken).ConfigureAwait(false))
            {
                return new CloseOutcome(true, confirmation, "Closed gracefully after answering its confirmation");
            }
        }

        return new CloseOutcome(false, confirmation, confirmation switch
        {
            CloseConfirmation.Answered => "IBKR Desktop stayed open after its confirmation was answered",
            CloseConfirmation.CouldNotAnswer => "IBKR Desktop is waiting for confirmation in its own window",
            _ => "IBKR Desktop did not exit after the close request",
        });
    }

    private readonly record struct CloseOutcome(bool Exited, CloseConfirmation Confirmation, string Message);

    public Task<AppState> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Select account", cancellationToken, ct =>
        {
            _failed = false;
            _lastError = null;
            _accounts.Select(accountId);
            _lastAction = $"Account {_accounts.Selected?.Alias ?? accountId} selected";
            _log.Info($"Selected account '{_accounts.Selected?.Alias}'");
            return Task.CompletedTask;
        });

    public Task<AppState> SaveAccountAsync(AccountEdit edit, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Save account", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;
            var saved = await _accounts.SaveAsync(edit, ct).ConfigureAwait(false);
            _lastAction = $"Saved account '{saved.Alias}'";
        });

    public Task<AppState> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Delete account", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;
            var alias = _accounts.List().FirstOrDefault(account => account.Id == accountId)?.Alias;
            await _accounts.DeleteAsync(accountId, ct).ConfigureAwait(false);
            _lastAction = alias is null ? "Account deleted" : $"Deleted account '{alias}'";
        });

    public Task<AppState> RestartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("Restart", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;
            EnsureInstalled();

            _stopping = true;
            if (_process.IsRunning)
            {
                var outcome = await TryCloseAsync(ct).ConfigureAwait(false);
                if (!outcome.Exited)
                {
                    _stopping = false;
                    throw new AppManagerException(
                        $"Restart needs the running app to close, but {outcome.Message.ToLowerInvariant()}. " +
                        "Nothing was terminated: answer the app's confirmation, or use Force Quit and then Start.");
                }
            }

            await LaunchAsync(ct).ConfigureAwait(false);
        });

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        _stopping = false;
        _log.Info($"Launching {_installation!.ExecutablePath}");
        await _process.StartAsync(_installation, cancellationToken).ConfigureAwait(false);
        _lastAction = $"Launched {Profile.DisplayName}";
        await WaitForWindowAsync(Profile.LoginWindowTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForWindowAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_process.HasVisibleWindow || timeout <= TimeSpan.Zero)
        {
            return;
        }

        var deadline = _clock.UtcNow + timeout;
        while (_clock.UtcNow < deadline && !_process.HasVisibleWindow)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (!_process.HasVisibleWindow)
        {
            _log.Warn($"No IBKR Desktop window appeared within {timeout.TotalSeconds:0}s");
        }
    }

    private async Task<AppState> ExecuteAsync(string action, CancellationToken cancellationToken, Func<CancellationToken, Task> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AppState state;
        try
        {
            _busy = true;
            try
            {
                await body(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _lastAction = $"{action} cancelled";
                _stopping = false;
                throw;
            }
            catch (AppManagerException ex)
            {
                _stopping = false;
                _failed = true;
                _lastError = ex.Message;
                _log.Warn($"{action}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _stopping = false;
                _failed = true;
                _lastError = $"{action} failed: {ex.Message}";
                _log.Error($"{action} failed", ex);
            }
            finally
            {
                _busy = false;
            }

            state = BuildState();
        }
        finally
        {
            _gate.Release();
        }

        Raise(state);
        return state;
    }

    private AppInstallation? LocateIfNeeded() => _installation ??= _locator.Locate();

    private void EnsureInstalled()
    {
        if (LocateIfNeeded() is null)
        {
            throw new AppManagerException(
                $"{Profile.DisplayName} is not installed (looked for {Profile.ExecutableName}). Set the executable path in Settings.");
        }
    }

    private AppState BuildState()
    {
        var installation = LocateIfNeeded();
        _state = new AppState
        {
            Status = ComputeStatus(installation),
            Installation = installation,
            ProcessId = _process.ProcessId,
            StartedAt = _process.StartedAt,
            LastAttempt = _lastAttempt,
            Credentials = CachedCredentials(),
            Accounts = CachedAccounts(),
            SelectedAccountId = _accounts.SelectedId,
            LastError = _lastError,
            LastAction = _lastAction,
            IsBusy = _busy,
            UpdatedAt = _clock.UtcNow,
        };
        return _state;
    }

    /// <summary>
    /// Status is derived from facts only: installed? process running? No window inspection happens
    /// here — the manager acts on the app through explicit commands and reports their outcome.
    /// </summary>
    private AppStatus ComputeStatus(AppInstallation? installation)
    {
        if (installation is null)
        {
            return AppStatus.NotInstalled;
        }

        if (!_process.IsRunning)
        {
            return _failed ? AppStatus.Failed : AppStatus.Stopped;
        }

        return _stopping ? AppStatus.Stopping : AppStatus.Running;
    }

    /// <summary>
    /// Account summaries and the resolved pair are cached per <see cref="IAccountService.Version"/>:
    /// the 2 s tick only needs presence flags, and re-reading Credential Manager (which decrypts the
    /// password) every two seconds helps nobody.
    /// </summary>
    private void RefreshAccountCache()
    {
        if (_accountsVersion == _accounts.Version)
        {
            return;
        }

        _accountsVersion = _accounts.Version;
        _accountSummaries = _accounts.List();
        _resolved = _accounts.Resolve();
    }

    private IReadOnlyList<AccountSummary> CachedAccounts()
    {
        RefreshAccountCache();
        return _accountSummaries;
    }

    private ResolvedCredentials CachedCredentials()
    {
        RefreshAccountCache();
        return _resolved;
    }

    private void Raise(AppState state)
    {
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _log.Error("A state change handler threw", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}

public sealed class AppManagerException(string message, Exception? inner = null) : Exception(message, inner);
