namespace IbkrManager.Core;

/// <summary>A named set of credentials. Persisted in settings; the secrets are not.</summary>
public sealed record AccountProfile
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Shown everywhere instead of the username: "Main", "Paper", "Client A".</summary>
    public string Alias { get; init; } = string.Empty;
}

/// <summary>What the UI may know about an account: its alias and whether both halves exist.</summary>
public sealed record AccountSummary(string Id, string Alias, bool HasUsername, bool HasPassword, CredentialSource Source)
{
    public bool IsComplete => HasUsername && HasPassword;

    public string Presence => IsComplete
        ? $"username ✓ · password ✓ ({Source.ToDisplayString()})"
        : $"username {(HasUsername ? "✓" : "✗")} · password {(HasPassword ? "✓" : "✗")}";

    public override string ToString() => $"{Alias} ({Presence})";
}

/// <summary>One save from the accounts dialog. A null <see cref="Id"/> creates an account.</summary>
public sealed record AccountEdit(
    string? Id,
    string Alias,
    Secret? Username,
    Secret? Password,
    bool Persist = true);

/// <summary>Reads and writes the stored accounts. The selected one is what Auto Login uses.</summary>
public interface IAccountService
{
    /// <summary>Bumped whenever the account list, a secret or the selection changes.</summary>
    int Version { get; }

    IReadOnlyList<AccountSummary> List();

    string? SelectedId { get; }

    AccountSummary? Selected { get; }

    /// <summary>The credential pair of the selected account, with its alias attached.</summary>
    ResolvedCredentials Resolve();

    /// <summary>The credential pair of one account (used to prefill the editor with its username).</summary>
    ResolvedCredentials Resolve(string accountId);

    Task<AccountSummary> SaveAsync(AccountEdit edit, CancellationToken cancellationToken = default);

    Task DeleteAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Makes <paramref name="accountId"/> the one Auto Login uses, and the startup default.</summary>
    void Select(string accountId);
}

/// <summary>
/// Owns the account list: the aliases live in settings, the credential pairs in the secret store,
/// keyed by account id, and a typed-this-session overlay wins over the stored pair of the *same*
/// account (never another one). Also migrates the single pre-0.4 credential.
/// </summary>
public sealed class AccountService : IAccountService
{
    private const int MaxAliasLength = 40;

    private readonly IAppSettingsStore _settings;
    private readonly ICredentialStore _store;
    private readonly ICredentialStore _session;
    private readonly SecretRedactor? _redactor;
    private readonly IAppLog _log;

    public AccountService(
        IAppSettingsStore settings,
        ICredentialStore store,
        ICredentialStore session,
        IAppLog? log = null,
        SecretRedactor? redactor = null)
    {
        _settings = settings;
        _store = store;
        _session = session;
        _log = log ?? NullAppLog.Instance;
        _redactor = redactor;
        Migrate();
    }

    /// <summary>
    /// Bumped on every change. The manager caches summaries/credentials per version so the 2 s tick
    /// does not decrypt the stored password over and over.
    /// </summary>
    public int Version { get; private set; }

    public string? SelectedId
    {
        get
        {
            var settings = _settings.Load();
            if (settings.SelectedAccountId is { Length: > 0 } selected &&
                settings.Accounts.Any(account => account.Id == selected))
            {
                return selected;
            }

            return settings.Accounts.Count > 0 ? settings.Accounts[0].Id : null;
        }
    }

    public AccountSummary? Selected => SelectedId is { } id
        ? List().FirstOrDefault(account => account.Id == id)
        : null;

    public IReadOnlyList<AccountSummary> List()
    {
        var settings = _settings.Load();
        return settings.Accounts
            .Select(account => new AccountSummary(
                account.Id,
                account.Alias,
                Has(account.Id, CredentialKind.Username),
                Has(account.Id, CredentialKind.Password),
                SourceOf(account.Id)))
            .ToList();
    }

    public ResolvedCredentials Resolve() => Selected is { } account
        ? ResolveValues(account)
        : new ResolvedCredentials(
            null,
            null,
            CredentialSource.None,
            CredentialSource.None,
            "no account is selected",
            "no account is selected");

    public ResolvedCredentials Resolve(string accountId)
    {
        var account = List().FirstOrDefault(candidate => candidate.Id == accountId)
            ?? throw new AppManagerException("That account no longer exists.");
        return ResolveValues(account);
    }

    private ResolvedCredentials ResolveValues(AccountSummary account)
    {
        var username = Read(account.Id, CredentialKind.Username);
        var password = Read(account.Id, CredentialKind.Password);
        var usernameSource = SourceOf(account.Id, CredentialKind.Username);
        var passwordSource = SourceOf(account.Id, CredentialKind.Password);

        // Registering the values keeps them out of any log line that happens to echo them.
        _redactor?.Register(username);
        _redactor?.Register(password);

        return new ResolvedCredentials(
            Secret.From(username),
            Secret.From(password),
            usernameSource,
            passwordSource,
            usernameSource == CredentialSource.None ? "the username of this account is not set" : null,
            passwordSource == CredentialSource.None ? "the password of this account is not set" : null,
            account.Id,
            account.Alias);
    }

    public async Task<AccountSummary> SaveAsync(AccountEdit edit, CancellationToken cancellationToken = default)
    {
        var alias = NormalizeAlias(edit.Alias);
        var settings = _settings.Load();
        var existing = edit.Id is { Length: > 0 } id
            ? settings.Accounts.FirstOrDefault(account => account.Id == id)
            : null;

        if (edit.Id is { Length: > 0 } && existing is null)
        {
            throw new AppManagerException("That account no longer exists.");
        }

        if (settings.Accounts.Any(account =>
                account.Id != existing?.Id &&
                string.Equals(account.Alias, alias, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppManagerException($"An account called '{alias}' already exists.");
        }

        var profile = existing ?? new AccountProfile { Id = NewId(), Alias = alias };
        profile = profile with { Alias = alias };

        if (existing is null)
        {
            settings = settings with
            {
                Accounts = [.. settings.Accounts, profile],
                SelectedAccountId = settings.SelectedAccountId ?? profile.Id,
            };
        }
        else
        {
            settings = settings with
            {
                Accounts = settings.Accounts.Select(account => account.Id == profile.Id ? profile : account).ToList(),
            };
        }

        _settings.Save(settings);

        // The half that was not retyped is kept, wherever it currently lives (this session, then the
        // store). Reading only the store here used to delete a session-typed password.
        var username = edit.Username is { IsEmpty: false } typedUsername
            ? typedUsername
            : Secret.From(Read(profile.Id, CredentialKind.Username));
        var password = edit.Password is { IsEmpty: false } typedPassword
            ? typedPassword
            : Secret.From(Read(profile.Id, CredentialKind.Password));

        if (edit.Persist)
        {
            await _store.SaveAsync(profile.Id, username, password, cancellationToken).ConfigureAwait(false);
            await _session.ClearAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            _log.Info($"Saved account '{alias}' in the credential store");
        }
        else
        {
            await _session.SaveAsync(profile.Id, username, password, cancellationToken).ConfigureAwait(false);
            _log.Info($"Account '{alias}' is kept in memory only (not remembered)");
        }

        Version++;

        return List().First(account => account.Id == profile.Id);
    }

    public async Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Load();
        var account = settings.Accounts.FirstOrDefault(candidate => candidate.Id == accountId)
            ?? throw new AppManagerException("That account no longer exists.");

        var remaining = settings.Accounts.Where(candidate => candidate.Id != accountId).ToList();
        var selected = settings.SelectedAccountId == accountId
            ? remaining.FirstOrDefault()?.Id
            : settings.SelectedAccountId;

        _settings.Save(settings with { Accounts = remaining, SelectedAccountId = selected });
        Version++;

        await _store.ClearAsync(accountId, cancellationToken).ConfigureAwait(false);
        await _session.ClearAsync(accountId, cancellationToken).ConfigureAwait(false);
        _log.Info($"Deleted account '{account.Alias}' and its stored credential");
    }

    public void Select(string accountId)
    {
        var settings = _settings.Load();
        if (settings.Accounts.All(account => account.Id != accountId))
        {
            throw new AppManagerException("That account no longer exists.");
        }

        if (settings.SelectedAccountId != accountId)
        {
            _settings.Save(settings with { SelectedAccountId = accountId });
            Version++;
        }
    }

    // ---------------------------------------------------------------- storage

    private string? Read(string accountId, CredentialKind kind) =>
        _session.Read(accountId, kind) ?? _store.Read(accountId, kind);

    private bool Has(string accountId, CredentialKind kind) => Read(accountId, kind) is { Length: > 0 };

    /// <summary>
    /// A save is either remembered (credential store) or typed this session (memory), and the
    /// in-memory value always wins for its own account, so the UI can show what will actually be used.
    /// </summary>
    private CredentialSource SourceOf(string accountId)
    {
        if (SourceOf(accountId, CredentialKind.Username) == CredentialSource.Session ||
            SourceOf(accountId, CredentialKind.Password) == CredentialSource.Session)
        {
            return _session.Source;
        }

        return Has(accountId, CredentialKind.Username) || Has(accountId, CredentialKind.Password)
            ? _store.Source
            : CredentialSource.None;
    }

    private CredentialSource SourceOf(string accountId, CredentialKind kind)
    {
        if (_session.Read(accountId, kind) is { Length: > 0 })
        {
            return _session.Source;
        }

        return _store.Read(accountId, kind) is { Length: > 0 } ? _store.Source : CredentialSource.None;
    }

    private void Migrate()
    {
        var settings = _settings.Load();
        if (settings.Accounts.Count > 0 || settings.LegacyAccountsMigrated || !_store.IsSupported)
        {
            return;
        }

        // 0.3.x stored one un-named pair at IbkrDesktopManager/username|password.
        var username = _store.ReadLegacy(CredentialKind.Username);
        var password = _store.ReadLegacy(CredentialKind.Password);
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return;
        }

        var profile = new AccountProfile { Id = NewId(), Alias = "Default" };
        _store.SaveAsync(profile.Id, Secret.From(username), Secret.From(password)).GetAwaiter().GetResult();
        _settings.Save(settings with
        {
            Accounts = [profile],
            SelectedAccountId = profile.Id,
            LegacyAccountsMigrated = true,
        });
        _log.Info("Migrated the existing stored credential to the account 'Default'");

        // The old entries are left in place on purpose: a downgrade to 0.3.x still finds them.
    }

    private static string NormalizeAlias(string alias)
    {
        var trimmed = (alias ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new AppManagerException("An account needs a name.");
        }

        return trimmed.Length > MaxAliasLength ? trimmed[..MaxAliasLength] : trimmed;
    }

    private static string NewId() => Guid.NewGuid().ToString("n")[..10];
}
