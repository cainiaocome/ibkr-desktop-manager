using System.Collections.ObjectModel;
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
    private AccountSummary? _selectedAccount;
    private bool _syncingAccounts;

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
        ManageAccountsCommand = new RelayCommand(() => AccountsRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? AccountsRequested;

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

    /// <summary>The accounts the user can choose from, as reported by the manager.</summary>
    public ObservableCollection<AccountSummary> Accounts { get; } = [];

    /// <summary>
    /// Two-way with the combo box. Changing it saves the choice (which is also the startup default);
    /// it never types anything, never restarts the app and never touches a running session.
    /// </summary>
    public AccountSummary? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value) || _syncingAccounts || value is null)
            {
                return;
            }

            _ = SelectAccountAsync(value.Id);
        }
    }

    public string CredentialsText => _state.Credentials.AccountAlias is { } alias
        ? $"{alias}: username {(_state.Credentials.HasUsername ? "✓" : "✗")}   password {(_state.Credentials.HasPassword ? "✓" : "✗")}"
        : "no account selected";

    public string CredentialsDetail
    {
        get
        {
            var credentials = _state.Credentials;
            if (credentials.IsComplete)
            {
                return $"source: {credentials.SourcesDescription} · used by Auto Login and at startup";
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

    public RelayCommand ManageAccountsCommand { get; }

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

    private async Task SelectAccountAsync(string accountId)
    {
        _transientError = null;
        try
        {
            Apply(await Task.Run(() => _manager.SelectAccountAsync(accountId, _shutdown.Token)).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            // Windows is shutting down; nothing to report.
        }
        catch (Exception ex)
        {
            _transientError = ex.Message;
            _runtime.Log.Error("Selecting the account failed", ex);
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(HasError));
            Apply(_manager.State);
        }
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
        SyncAccounts(state);
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

    private void SyncAccounts(AppState state)
    {
        _syncingAccounts = true;
        try
        {
            var wanted = state.Accounts.Select(account => account.Id).ToList();
            if (!Accounts.Select(account => account.Id).SequenceEqual(wanted))
            {
                Accounts.Clear();
                foreach (var account in state.Accounts)
                {
                    Accounts.Add(account);
                }
            }
            else
            {
                for (var i = 0; i < state.Accounts.Count; i++)
                {
                    if (!Equals(Accounts[i], state.Accounts[i]))
                    {
                        Accounts[i] = state.Accounts[i];
                    }
                }
            }

            var selected = Accounts.FirstOrDefault(account => account.Id == state.SelectedAccountId);
            if (!Equals(_selectedAccount, selected))
            {
                _selectedAccount = selected;
                OnPropertyChanged(nameof(SelectedAccount));
            }
        }
        finally
        {
            _syncingAccounts = false;
        }
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
