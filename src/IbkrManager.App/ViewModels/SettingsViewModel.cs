namespace IbkrManager.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IPlatformRuntime _runtime;
    private string _executablePath;
    private string _usernameVariable;
    private string _passwordVariable;
    private string _loginDriver;
    private string _error = string.Empty;
    private bool _busy;

    public SettingsViewModel(IPlatformRuntime runtime)
    {
        _runtime = runtime;
        var settings = runtime.SettingsStore.Load();
        _executablePath = settings.ExecutablePathOverride ?? string.Empty;
        _usernameVariable = settings.UsernameVariable;
        _passwordVariable = settings.PasswordVariable;
        _loginDriver = settings.LoginDriver;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<string> LoginDrivers { get; } = ["auto", "manual"];

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    public string UsernameVariable
    {
        get => _usernameVariable;
        set => SetProperty(ref _usernameVariable, value);
    }

    public string PasswordVariable
    {
        get => _passwordVariable;
        set => SetProperty(ref _passwordVariable, value);
    }

    public string LoginDriver
    {
        get => _loginDriver;
        set => SetProperty(ref _loginDriver, value);
    }

    public string SettingsPath => Path.Combine(_runtime.DataDirectory, "settings.json");

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

    public RelayCommand CancelCommand { get; }

    private Task SaveAsync()
    {
        Error = string.Empty;

        var usernameVariable = UsernameVariable.Trim();
        var passwordVariable = PasswordVariable.Trim();
        if (string.IsNullOrWhiteSpace(usernameVariable) || string.IsNullOrWhiteSpace(passwordVariable))
        {
            Error = "Both environment variable names are required.";
            return Task.CompletedTask;
        }

        if (string.Equals(usernameVariable, passwordVariable, StringComparison.OrdinalIgnoreCase))
        {
            Error = "Username and password must use different environment variables.";
            return Task.CompletedTask;
        }

        var executablePath = ExecutablePath.Trim();
        if (!string.IsNullOrEmpty(executablePath) && !File.Exists(executablePath))
        {
            Error = $"Executable not found: {executablePath}";
            return Task.CompletedTask;
        }

        SetBusy(true);
        try
        {
            _runtime.SettingsStore.Save(new AppSettings
            {
                ExecutablePathOverride = string.IsNullOrEmpty(executablePath) ? null : executablePath,
                UsernameVariable = usernameVariable,
                PasswordVariable = passwordVariable,
                LoginDriver = string.Equals(LoginDriver, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto",
                PersistToCredentialManager = _runtime.SettingsStore.Load().PersistToCredentialManager,
                WriteUserEnvironmentVariables = _runtime.SettingsStore.Load().WriteUserEnvironmentVariables,
                StartWithWindows = _runtime.SettingsStore.Load().StartWithWindows,
                ShowTrayIcon = _runtime.SettingsStore.Load().ShowTrayIcon,
                CheckForUpdates = _runtime.SettingsStore.Load().CheckForUpdates,
            });

            _runtime.Log.Info("Settings saved");
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _runtime.Log.Error("Saving settings failed", ex);
            Error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        return Task.CompletedTask;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveCommand.RaiseCanExecuteChanged();
    }
}
