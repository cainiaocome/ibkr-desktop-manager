namespace IbkrManager.Core;

/// <summary>User-editable settings. Persisted as JSON next to the logs, never containing secrets.</summary>
public sealed record AppSettings
{
    public static readonly AppSettings Default = new();

    public string? ExecutablePathOverride { get; init; }

    public string UsernameVariable { get; init; } = "IBKR_USERNAME";

    public string PasswordVariable { get; init; } = "IBKR_PASSWORD";

    /// <summary>"auto" (clicks and types into the login window) or "manual" (never types).</summary>
    public string LoginDriver { get; init; } = "auto";

    public bool PersistToCredentialManager { get; init; } = true;

    public bool WriteUserEnvironmentVariables { get; init; }

    public bool StartWithWindows { get; init; }

    public bool ShowTrayIcon { get; init; }

    public bool CheckForUpdates { get; init; }

    public AppSettings Normalize() => this with
    {
        UsernameVariable = string.IsNullOrWhiteSpace(UsernameVariable) ? "IBKR_USERNAME" : UsernameVariable.Trim(),
        PasswordVariable = string.IsNullOrWhiteSpace(PasswordVariable) ? "IBKR_PASSWORD" : PasswordVariable.Trim(),
        // Anything that is not "manual" means "do it automatically"; older stores wrote
        // "sendinput" or "uia" for that.
        LoginDriver = string.Equals(LoginDriver, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto",
    };
}

public interface IAppSettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>In-memory settings store, used by tests and as a fallback when disk access fails.</summary>
public sealed class InMemorySettingsStore(AppSettings? initial = null) : IAppSettingsStore
{
    private AppSettings _settings = (initial ?? AppSettings.Default).Normalize();

    public AppSettings Current => _settings;

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings) => _settings = settings.Normalize();
}
