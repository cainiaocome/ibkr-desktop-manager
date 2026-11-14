namespace IbkrManager.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IPlatformRuntime _runtime;
    private string _executablePath;
    private string _loginDriver;
    private string _error = string.Empty;
    private bool _busy;

    public SettingsViewModel(IPlatformRuntime runtime)
    {
        _runtime = runtime;
        var settings = runtime.SettingsStore.Load();
        _executablePath = settings.ExecutablePathOverride ?? string.Empty;
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

        var executablePath = ExecutablePath.Trim();
        if (!string.IsNullOrEmpty(executablePath) && !File.Exists(executablePath))
        {
            Error = $"Executable not found: {executablePath}";
            return Task.CompletedTask;
        }

        SetBusy(true);
        try
        {
            // Only the fields this dialog owns; accounts and their selection must survive a save.
            _runtime.SettingsStore.Save(_runtime.SettingsStore.Load() with
            {
                ExecutablePathOverride = string.IsNullOrEmpty(executablePath) ? null : executablePath,
                LoginDriver = string.Equals(LoginDriver, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto",
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
