namespace IbkrManager.Core;

/// <summary>User-editable settings. Persisted as JSON next to the logs, never containing secrets.</summary>
public sealed record AppSettings
{
    public static readonly AppSettings Default = new();

    public string? ExecutablePathOverride { get; init; }

    /// <summary>Named accounts. Aliases only — the credential pairs live in the secret store.</summary>
    public IReadOnlyList<AccountProfile> Accounts { get; init; } = [];

    /// <summary>The account Auto Login uses; also the default when the manager starts.</summary>
    public string? SelectedAccountId { get; init; }

    /// <summary>"auto" (clicks and types into the login window) or "manual" (never types).</summary>
    public string LoginDriver { get; init; } = "auto";

    /// <summary>Default for the "remember" checkbox in the accounts dialog.</summary>
    public bool PersistToCredentialManager { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public bool ShowTrayIcon { get; init; }

    public bool CheckForUpdates { get; init; }

    public AppSettings Normalize()
    {
        // Aliases are cosmetic, ids are keys: repair the list here once instead of in every consumer.
        var accounts = new List<AccountProfile>();
        foreach (var account in Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id) ||
                accounts.Any(existing => existing.Id == account.Id))
            {
                continue;
            }

            var alias = string.IsNullOrWhiteSpace(account.Alias)
                ? $"Account {accounts.Count + 1}"
                : account.Alias.Trim();
            accounts.Add(account with { Alias = alias });
        }

        var selected = accounts.Any(account => account.Id == SelectedAccountId)
            ? SelectedAccountId
            : accounts.FirstOrDefault()?.Id;

        return this with
        {
            Accounts = accounts,
            SelectedAccountId = selected,
            // Anything that is not "manual" means "do it automatically"; older stores wrote
            // "sendinput" or "uia" for that.
            LoginDriver = string.Equals(LoginDriver, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto",
        };
    }
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
