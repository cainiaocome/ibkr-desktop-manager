using System.Globalization;

namespace IbkrManager.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IAppManager _manager;
    private readonly IPlatformRuntime _runtime;
    private readonly IClock _clock;
    private AppState _state = AppState.Initial;
    private string? _transientError;
    private bool _busy;

    public MainWindowViewModel(IAppManager manager, IPlatformRuntime runtime, IClock? clock = null)
    {
        _manager = manager;
        _runtime = runtime;
        _clock = clock ?? SystemClock.Instance;

        StartCommand = new AsyncRelayCommand(() => RunAsync(ct => _manager.StartAsync(ct)), () => CanStart);
        AutoLoginCommand = new AsyncRelayCommand(() => RunAsync(ct => _manager.AutoLoginAsync(ct)), () => CanAutoLogin);
        QuitCommand = new AsyncRelayCommand(() => RunAsync(ct => _manager.QuitAsync(force: false, ct)), () => CanQuit);
        ForceQuitCommand = new AsyncRelayCommand(
            () => RunAsync(ct => _manager.QuitAsync(force: true, ct)),
            () => CanQuit);
        RestartCommand = new AsyncRelayCommand(() => RunAsync(ct => _manager.RestartAsync(ct)), () => CanRestart);
        OpenLogsCommand = new RelayCommand(() => _runtime.OpenInFileExplorer(_runtime.LogDirectory));
        OpenCredentialsCommand = new RelayCommand(() => CredentialsRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? CredentialsRequested;

    public event EventHandler? SettingsRequested;

    public string Title => $"{_runtime.Profile.DisplayName} Manager";

    public IPlatformRuntime Runtime => _runtime;

    public string StatusText => _state.Status.ToDisplayString();

    public string StatusDetail => _state switch
    {
        { Installation: null } =>
            $"{_runtime.Profile.DisplayName} was not found ({_runtime.Profile.ExecutableName}). Install it, or set the executable path in Settings.",
        { IsRunning: true, ProcessId: { } pid } => $"PID {pid} · up {UptimeText()}",
        _ => _state.Installation?.ExecutablePath ?? string.Empty,
    };

    public string VersionText => _state.Installation is { } installation
        ? string.IsNullOrWhiteSpace(installation.Version) ? "unknown version" : $"v{installation.Version}"
        : "not detected";

    public string CredentialsText => _state.Credentials.IsComplete
        ? "username ✓   password ✓"
        : $"username {(_state.Credentials.HasUsername ? "✓" : "✗")}   password {(_state.Credentials.HasPassword ? "✓" : "✗")}";

    public string CredentialsDetail
    {
        get
        {
            var credentials = _state.Credentials;
            if (credentials.IsComplete)
            {
                return $"source: {credentials.SourcesDescription}";
            }

            var hints = new List<string>();
            if (!credentials.HasUsername)
            {
                hints.Add(credentials.UsernameHint ?? "username is not set");
            }

            if (!credentials.HasPassword)
            {
                hints.Add(credentials.PasswordHint ?? "password is not set");
            }

            return string.Join(" · ", hints);
        }
    }

    /// <summary>
    /// What the last Auto Login did. The manager deliberately does not track the app's UI state, so
    /// this reports the outcome of the button press rather than claiming to know where the app is now.
    /// </summary>
    public string LoginStateText => _state.LastAttempt switch
    {
        null => "No auto login yet — press Auto Login after the app shows its login form",
        { AwaitingTwoFactor: true } =>
            $"Auto login submitted at {_state.LastAttempt.At.ToLocalTime():HH:mm:ss} — enter the 2FA code in the IBKR window",
        { Success: true } =>
            $"Auto login submitted at {_state.LastAttempt.At.ToLocalTime():HH:mm:ss} — check the IBKR window",
        _ => $"Last auto login failed at {_state.LastAttempt.At.ToLocalTime():HH:mm:ss} — see the error above",
    };

    public string ErrorText => _transientError ?? _state.LastError ?? string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string LastActionText => _state.LastAction ?? string.Empty;

    public bool IsBusy => _busy || _state.IsBusy;

    public bool CanStart => _state.Installation is not null && !_state.IsRunning && !IsBusy;

    public bool CanAutoLogin => _state.Installation is not null && !IsBusy;

    public bool CanQuit => _state.IsRunning && !IsBusy;

    public bool CanRestart => _state.Installation is not null && !IsBusy;

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand AutoLoginCommand { get; }

    public AsyncRelayCommand QuitCommand { get; }

    /// <summary>Terminates the process tree — the explicit way out when the app will not close.</summary>
    public AsyncRelayCommand ForceQuitCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand OpenCredentialsCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>
    /// Cancelled when Windows ends the session, so an in-flight command stops instead of holding the
    /// shutdown open.
    /// </summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Stops background work; called from the session-end hook.</summary>
    public void PrepareForShutdown()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }
    }

    public async Task InitializeAsync()
    {
        Apply(await Task.Run(() => _manager.InitializeAsync()).ConfigureAwait(true));
    }

    public async Task RefreshAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        // Always off the UI thread: the window must keep pumping messages so Windows never sees it as
        // "not responding" — that is what stalls a shutdown.
        var state = await Task.Run(() => _manager.TickAsync(_shutdown.Token)).ConfigureAwait(true);
        Apply(state);
    }

    private async Task RunAsync(Func<CancellationToken, Task<AppState>> action)
    {
        _transientError = null;
        SetBusy(true);
        try
        {
            Apply(await Task.Run(() => action(_shutdown.Token)).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            _transientError = "Cancelled because Windows is shutting down";
        }
        catch (Exception ex)
        {
            _transientError = ex.Message;
            _runtime.Log.Error("Command failed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Apply(AppState state)
    {
        _state = state;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(CredentialsText));
        OnPropertyChanged(nameof(CredentialsDetail));
        OnPropertyChanged(nameof(LoginStateText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(LastActionText));
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommandStates();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanAutoLogin));
        OnPropertyChanged(nameof(CanQuit));
        OnPropertyChanged(nameof(CanRestart));
        StartCommand.RaiseCanExecuteChanged();
        AutoLoginCommand.RaiseCanExecuteChanged();
        QuitCommand.RaiseCanExecuteChanged();
        ForceQuitCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private string UptimeText()
    {
        if (_state.StartedAt is not { } started)
        {
            return "—";
        }

        var uptime = _clock.UtcNow - started;
        return uptime.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)uptime.TotalHours}:{uptime.Minutes:00}:{uptime.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{uptime.Minutes:00}:{uptime.Seconds:00}");
    }
}
