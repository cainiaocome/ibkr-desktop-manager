using System.Collections.ObjectModel;

namespace IbkrManager.App.ViewModels;

/// <summary>One row in the accounts list: alias plus whether both halves are stored.</summary>
public sealed class AccountRow(string id, string alias, string presence, bool inUse)
{
    public string Id { get; } = id;

    public string Alias { get; } = alias;

    public string Presence { get; } = presence;

    public bool InUse { get; } = inUse;

    public string Title => InUse ? $"{Alias}  ·  in use" : Alias;

    public override string ToString() => Title;
}

/// <summary>
/// The accounts dialog: a list of named credential pairs, with an editor for one at a time.
/// Usernames are shown (they identify which account an alias means); passwords are never pre-filled.
/// </summary>
public sealed class AccountsViewModel : ViewModelBase
{
    private readonly IAppManager _manager;
    private readonly IPlatformRuntime _runtime;

    private string? _accountId;
    private string _alias = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _loadedUsername = string.Empty;
    private bool _hasStoredUsername;
    private bool _hasStoredPassword;
    private bool _remember = true;
    private string _error = string.Empty;
    private bool _busy;
    private bool _deleteArmed;
    private AccountRow? _selected;

    public AccountsViewModel(IAppManager manager, IPlatformRuntime runtime)
    {
        _manager = manager;
        _runtime = runtime;
        _remember = runtime.SettingsStore.Load().PersistToCredentialManager;

        NewCommand = new RelayCommand(() => StartNew());
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !_busy && _accountId is not null);
        UseCommand = new AsyncRelayCommand(UseAsync, () => !_busy && _accountId is not null);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));

        Reload();
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    public string? AccountId => _accountId;

    public bool IsNew => _accountId is null;

    public string EditorTitle => IsNew ? "New account" : $"Edit “{_alias}”";

    public AccountRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                Load(value);
            }
        }
    }

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                OnPropertyChanged(nameof(EditorTitle));
            }
        }
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string PasswordWatermark => _hasStoredPassword
        ? "stored — type to replace"
        : "IBKR password";

    public bool Remember
    {
        get => _remember;
        set => SetProperty(ref _remember, value);
    }

    public string RememberHint => Remember
        ? "Saved in Windows Credential Manager (per Windows user, DPAPI)."
        : "Kept in memory until the manager exits — Auto Login then needs it again.";

    public string DeleteLabel => _deleteArmed ? "Really delete?" : "Delete";

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public AsyncRelayCommand UseCommand { get; }

    public RelayCommand NewCommand { get; }

    public RelayCommand CloseCommand { get; }

    private void Reload()
    {
        var state = _manager.State;
        Accounts.Clear();
        foreach (var account in state.Accounts)
        {
            Accounts.Add(new AccountRow(
                account.Id,
                account.Alias,
                account.Presence,
                account.Id == state.SelectedAccountId));
        }

        var current = Accounts.FirstOrDefault(row => row.Id == _accountId);
        if (current is not null)
        {
            _selected = current;
            OnPropertyChanged(nameof(SelectedRow));
        }
        else if (_accountId is null)
        {
            _selected = null;
            OnPropertyChanged(nameof(SelectedRow));
        }

        RaiseCommandStates();
    }

    private void StartNew()
    {
        _accountId = null;
        _loadedUsername = string.Empty;
        _hasStoredUsername = false;
        _hasStoredPassword = false;
        _deleteArmed = false;
        Alias = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        Error = string.Empty;
        _selected = null;
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(EditorTitle));
        RaiseCommandStates();
    }

    private void Load(AccountRow? row)
    {
        if (row is null || row.Id == _accountId && row.Id == _selected?.Id)
        {
            return;
        }

        _accountId = row.Id;
        _deleteArmed = false;
        Error = string.Empty;

        ResolvedCredentials credentials;
        try
        {
            credentials = _runtime.Accounts.Resolve(row.Id);
        }
        catch (AppManagerException ex)
        {
            // The list came from state a moment ago; the account is gone (deleted in another window).
            Error = ex.Message;
            credentials = ResolvedCredentials.Empty;
        }

        _loadedUsername = credentials.Username?.Reveal() ?? string.Empty;
        _hasStoredUsername = credentials.HasUsername;
        _hasStoredPassword = credentials.HasPassword;

        Alias = row.Alias;
        Username = _loadedUsername;
        Password = string.Empty;

        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PasswordWatermark));
        RaiseCommandStates();
    }

    private async Task SaveAsync()
    {
        Error = string.Empty;
        var alias = Alias.Trim();
        if (alias.Length == 0)
        {
            Error = "Give the account a name, e.g. “Main”.";
            return;
        }

        var usernameChanged = !string.Equals(Username.Trim(), _loadedUsername, StringComparison.Ordinal);
        var usernameTyped = Username.Trim();
        var passwordTyped = Password;

        if (IsNew && (usernameTyped.Length == 0 || passwordTyped.Length == 0))
        {
            Error = "A new account needs a username and a password.";
            return;
        }

        if (!IsNew && !_hasStoredUsername && usernameTyped.Length == 0)
        {
            Error = "This account has no username yet.";
            return;
        }

        if (!IsNew && !_hasStoredPassword && passwordTyped.Length == 0)
        {
            Error = "This account has no password yet.";
            return;
        }

        SetBusy(true);
        try
        {
            using var username = usernameChanged && usernameTyped.Length > 0 ? new Secret(usernameTyped) : null;
            using var password = passwordTyped.Length > 0 ? new Secret(passwordTyped) : null;

            var state = await _manager
                .SaveAccountAsync(new AccountEdit(_accountId, alias, username, password, Remember))
                .ConfigureAwait(true);

            _runtime.SettingsStore.Save(_runtime.SettingsStore.Load() with { PersistToCredentialManager = Remember });
            Password = string.Empty;
            Reload();

            var saved = state.Accounts.FirstOrDefault(account => account.Alias == alias);
            if (saved is not null)
            {
                _selected = Accounts.FirstOrDefault(row => row.Id == saved.Id);
                OnPropertyChanged(nameof(SelectedRow));
            }

            _accountId = saved?.Id ?? _accountId;
            _loadedUsername = usernameTyped;
            _hasStoredUsername = saved?.HasUsername ?? _hasStoredUsername;
            _hasStoredPassword = saved?.HasPassword ?? _hasStoredPassword;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(EditorTitle));
            OnPropertyChanged(nameof(PasswordWatermark));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            _runtime.Log.Error("Saving the account failed", ex);
            Error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeleteAsync()
    {
        if (_accountId is null)
        {
            return;
        }

        if (!_deleteArmed)
        {
            _deleteArmed = true;
            OnPropertyChanged(nameof(DeleteLabel));
            return;
        }

        Error = string.Empty;
        SetBusy(true);
        try
        {
            var deleted = _accountId;
            await _manager.DeleteAccountAsync(deleted).ConfigureAwait(true);
            _accountId = null;
            _deleteArmed = false;
            StartNew();
            Reload();
        }
        catch (Exception ex)
        {
            _runtime.Log.Error("Deleting the account failed", ex);
            Error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UseAsync()
    {
        if (_accountId is null)
        {
            return;
        }

        Error = string.Empty;
        SetBusy(true);
        try
        {
            await _manager.SelectAccountAsync(_accountId).ConfigureAwait(true);
            Reload();
        }
        catch (Exception ex)
        {
            _runtime.Log.Error("Selecting the account failed", ex);
            Error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        UseCommand.RaiseCanExecuteChanged();
    }
}
