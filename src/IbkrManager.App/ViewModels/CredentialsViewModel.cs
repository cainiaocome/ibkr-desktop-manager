namespace IbkrManager.App.ViewModels;

/// <summary>
/// The credential setup dialog. Reads the resolved state once so it can show where values
/// currently come from, but never pre-fills the stored password.
/// </summary>
public sealed class CredentialsViewModel : ViewModelBase
{
    private readonly IPlatformRuntime _runtime;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _remember = true;
    private bool _alsoWriteEnvironment;
    private string _error = string.Empty;
    private bool _busy;

    public CredentialsViewModel(IPlatformRuntime runtime)
    {
        _runtime = runtime;
        var settings = runtime.SettingsStore.Load();
        var credentials = runtime.Manager.State.Credentials;

        if (credentials.HasUsername)
        {
            _username = credentials.Username!.Reveal();
        }

        HasStoredPassword = credentials.HasPassword;
        _remember = settings.PersistToCredentialManager;
        _alsoWriteEnvironment = settings.WriteUserEnvironmentVariables;
        CurrentSource = credentials.IsComplete
            ? $"Currently using {credentials.SourcesDescription}"
            : credentials.MissingDescription;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => !_busy);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

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

    public bool HasStoredPassword { get; }

    public string PasswordWatermark => HasStoredPassword
        ? "stored — type to replace"
        : "IBKR password";

    public bool Remember
    {
        get => _remember;
        set => SetProperty(ref _remember, value);
    }

    public bool AlsoWriteEnvironment
    {
        get => _alsoWriteEnvironment;
        set => SetProperty(ref _alsoWriteEnvironment, value);
    }

    public string CurrentSource { get; }

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

    public AsyncRelayCommand ClearCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        Error = string.Empty;
        if (string.IsNullOrWhiteSpace(Username))
        {
            Error = "Username is required.";
            return;
        }

        if (string.IsNullOrEmpty(Password) && !HasStoredPassword)
        {
            Error = "Password is required.";
            return;
        }

        SetBusy(true);
        try
        {
            using var username = new Secret(Username.Trim());
            using var password = string.IsNullOrEmpty(Password) ? null : new Secret(Password);

            var options = new CredentialWriteOptions(Remember, AlsoWriteEnvironment);
            await _runtime.CredentialStore.SaveAsync(username, password, options).ConfigureAwait(true);

            if (Remember || AlsoWriteEnvironment)
            {
                await _runtime.SessionStore.ClearAsync().ConfigureAwait(true);
            }
            else
            {
                await _runtime.SessionStore.SaveAsync(username, password, CredentialWriteOptions.SessionOnly).ConfigureAwait(true);
            }

            var settings = _runtime.SettingsStore.Load() with
            {
                PersistToCredentialManager = Remember,
                WriteUserEnvironmentVariables = AlsoWriteEnvironment,
            };
            _runtime.SettingsStore.Save(settings);
            _runtime.Log.Info("Credentials saved from the setup dialog");

            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _runtime.Log.Error("Saving credentials failed", ex);
            Error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ClearAsync()
    {
        Error = string.Empty;
        SetBusy(true);
        try
        {
            await _runtime.CredentialStore.ClearAsync().ConfigureAwait(true);
            await _runtime.SessionStore.ClearAsync().ConfigureAwait(true);
            Username = string.Empty;
            Password = string.Empty;
            _runtime.Log.Info("Credentials cleared");
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _runtime.Log.Error("Clearing credentials failed", ex);
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
        SaveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }
}
