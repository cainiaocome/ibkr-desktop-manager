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
    private readonly ICredentialResolver _credentials;
    private readonly ILoginDriver _loginDriver;
    private readonly IClock _clock;
    private readonly IAppLog _log;
    private readonly IWindowInspector _inspector;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppInstallation? _installation;
    private string? _lastError;
    private string? _lastAction;
    private bool _failed;
    private bool _busy;
    private bool _disposed;
    private DateTimeOffset? _launchRequestedAt;
    private DateTimeOffset? _twoFactorSince;
    private AppState _state = AppState.Initial;

    public AppManager(
        IAppLocator locator,
        IProcessController process,
        ICredentialResolver credentials,
        ILoginDriver loginDriver,
        AppProfile profile,
        IClock clock,
        IAppLog log,
        IWindowInspector? inspector = null,
        TimeSpan? pollInterval = null)
    {
        _locator = locator;
        _process = process;
        _credentials = credentials;
        _loginDriver = loginDriver;
        Profile = profile;
        _clock = clock;
        _log = log;
        _inspector = inspector ?? NullWindowInspector.Instance;
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
            _twoFactorSince = null;

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
            _twoFactorSince = null;

            var credentials = _credentials.Resolve();
            if (!credentials.IsComplete)
            {
                var hint = !credentials.HasUsername
                    ? credentials.UsernameHint ?? "username is not set"
                    : credentials.PasswordHint ?? "password is not set";
                throw new AppManagerException(
                    $"Auto Login blocked: {hint}. Add it in the Credentials dialog or set the environment variable.");
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

            if (!result.Success)
            {
                throw new AppManagerException($"Auto login failed: {result.Message}");
            }

            _lastAction = result.Message;
            if (result.ReachedTwoFactor)
            {
                _twoFactorSince = _clock.UtcNow;
            }
        });

    public Task<AppState> QuitAsync(bool force = false, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Quit", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;
            _twoFactorSince = null;
            _launchRequestedAt = null;

            if (!_process.IsRunning)
            {
                _lastAction = "Already stopped";
                return;
            }

            if (force)
            {
                await _process.KillAsync(ct).ConfigureAwait(false);
                _lastAction = "Terminated the process tree";
                _log.Warn("Force-quit IBKR Desktop");
                return;
            }

            var requested = await _process.CloseMainWindowAsync(ct).ConfigureAwait(false);
            var exited = requested && await _process.WaitForExitAsync(Profile.GracefulShutdownTimeout, ct).ConfigureAwait(false);

            if (exited)
            {
                _lastAction = "Closed gracefully";
                _log.Info("IBKR Desktop closed gracefully");
                return;
            }

            _log.Warn("IBKR Desktop did not exit after the graceful close request; terminating the process tree");
            await _process.KillAsync(ct).ConfigureAwait(false);
            _lastAction = "Terminated after the graceful close timed out";
        });

    public Task<AppState> RestartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("Restart", cancellationToken, async ct =>
        {
            _failed = false;
            _lastError = null;
            EnsureInstalled();

            if (_process.IsRunning)
            {
                var requested = await _process.CloseMainWindowAsync(ct).ConfigureAwait(false);
                var exited = requested && await _process.WaitForExitAsync(Profile.GracefulShutdownTimeout, ct).ConfigureAwait(false);
                if (!exited)
                {
                    _log.Warn("Restart: graceful close timed out, terminating the process tree");
                    await _process.KillAsync(ct).ConfigureAwait(false);
                }
            }

            _twoFactorSince = null;
            await LaunchAsync(ct).ConfigureAwait(false);
        });

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        _launchRequestedAt = _clock.UtcNow;
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
                throw;
            }
            catch (AppManagerException ex)
            {
                _failed = true;
                _lastError = ex.Message;
                _log.Warn($"{action}: {ex.Message}");
            }
            catch (Exception ex)
            {
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
            WindowTitle = _process.MainWindowTitle,
            Credentials = _credentials.Resolve(),
            LastError = _lastError,
            LastAction = _lastAction,
            IsBusy = _busy,
            UpdatedAt = _clock.UtcNow,
        };
        return _state;
    }

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

        var kind = ClassifyWindow();
        return kind switch
        {
            WindowKind.LoggedIn => AppStatus.LoggedIn,
            WindowKind.TwoFactor => AppStatus.AwaitingTwoFactor,
            WindowKind.Login => _twoFactorSince is null ? AppStatus.AwaitingLogin : AppStatus.AwaitingTwoFactor,
            _ => InLaunchGracePeriod() ? AppStatus.Launching : _twoFactorSince is null ? AppStatus.AwaitingLogin : AppStatus.AwaitingTwoFactor,
        };
    }

    /// <summary>
    /// Accessibility first, title second: IBKR Desktop's window title never changes between the
    /// login screen and the logged-in app.
    /// </summary>
    private WindowKind ClassifyWindow()
    {
        UiObservation? observation = null;
        try
        {
            observation = _inspector.Observe();
        }
        catch (Exception ex)
        {
            _log.Warn($"UI Automation inspection failed: {ex.Message}");
        }

        return UiClassifier.Classify(Profile, _process.MainWindowTitle, observation);
    }

    private bool InLaunchGracePeriod() =>
        _launchRequestedAt is { } requested && _clock.UtcNow - requested < Profile.LaunchTimeout;

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
