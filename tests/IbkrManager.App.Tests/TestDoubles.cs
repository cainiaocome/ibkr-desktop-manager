namespace IbkrManager.App.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeAppManager : IAppManager
{
    public AppState State { get; set; } = AppState.Initial;

    public AppProfile Profile { get; set; } = AppProfile.Default;

    public AppState? NextResult { get; set; }

    public int StartCalls { get; private set; }

    public int AutoLoginCalls { get; private set; }

    public int QuitCalls { get; private set; }

    public int RestartCalls { get; private set; }

    public int SelectAccountCalls { get; private set; }

    public int SaveAccountCalls { get; private set; }

    public int DeleteAccountCalls { get; private set; }

    public string? LastSelectedAccountId { get; private set; }

    public AccountEdit? LastAccountEdit { get; private set; }

    /// <summary>The edit with the secrets revealed, captured while they are still alive.</summary>
    public AccountEditValues? LastEditValues { get; private set; }

    public int TickCalls { get; private set; }

    /// <summary>Whether each TickAsync ran on a thread-pool thread (i.e. off the UI thread).</summary>
    public List<bool> TickThreadPool { get; } = [];

    public event EventHandler<AppState>? StateChanged;

    public Task<AppState> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

    public Task<AppState> TickAsync(CancellationToken cancellationToken = default)
    {
        TickCalls++;
        TickThreadPool.Add(Thread.CurrentThread.IsThreadPoolThread);
        return Task.FromResult(State);
    }

    public Task<AppState> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        return Complete();
    }

    public Task<AppState> AutoLoginAsync(CancellationToken cancellationToken = default)
    {
        AutoLoginCalls++;
        return Complete();
    }

    public Task<AppState> QuitAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        QuitCalls++;
        return Complete();
    }

    public Task<AppState> RestartAsync(CancellationToken cancellationToken = default)
    {
        RestartCalls++;
        return Complete();
    }

    public Task<AppState> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        SelectAccountCalls++;
        LastSelectedAccountId = accountId;
        State = State with { SelectedAccountId = accountId };
        return Complete();
    }

    public Task<AppState> SaveAccountAsync(AccountEdit edit, CancellationToken cancellationToken = default)
    {
        SaveAccountCalls++;
        LastAccountEdit = edit;
        LastEditValues = new AccountEditValues(
            edit.Id,
            edit.Alias,
            edit.Username?.Reveal(),
            edit.Password?.Reveal(),
            edit.Persist);
        var id = edit.Id ?? "new-" + (State.Accounts.Count + 1);
        var others = State.Accounts.Where(account => account.Id != id).ToList();
        var previous = State.Accounts.FirstOrDefault(account => account.Id == id);
        others.Add(new AccountSummary(
            id,
            edit.Alias,
            edit.Username is not null || previous?.HasUsername == true,
            edit.Password is not null || previous?.HasPassword == true,
            CredentialSource.CredentialManager));
        State = State with
        {
            Accounts = others,
            SelectedAccountId = State.SelectedAccountId ?? id,
        };
        return Complete();
    }

    public Task<AppState> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        DeleteAccountCalls++;
        State = State with
        {
            Accounts = State.Accounts.Where(account => account.Id != accountId).ToList(),
            SelectedAccountId = State.SelectedAccountId == accountId
                ? State.Accounts.FirstOrDefault(account => account.Id != accountId)?.Id
                : State.SelectedAccountId,
        };
        return Complete();
    }

    private Task<AppState> Complete()
    {
        if (NextResult is not null)
        {
            State = NextResult;
        }

        StateChanged?.Invoke(this, State);
        return Task.FromResult(State);
    }
}

internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, (string? Username, string? Password)> _values = [];

    public int SaveCalls { get; private set; }

    public int ClearCalls { get; private set; }

    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "fake store";

    public bool IsSupported => true;

    public string? Read(string accountId, CredentialKind kind) =>
        _values.TryGetValue(accountId, out var value)
            ? kind == CredentialKind.Username ? value.Username : value.Password
            : null;

    public bool HasStoredValue(string accountId, CredentialKind kind) => Read(accountId, kind) is { Length: > 0 };

    public string? ReadLegacy(CredentialKind kind) => null;

    public Task SaveAsync(string accountId, Secret? username, Secret? password, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        _values[accountId] = (username?.Reveal(), password?.Reveal());
        return Task.CompletedTask;
    }

    public Task ClearAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ClearCalls++;
        _values.Remove(accountId);
        return Task.CompletedTask;
    }
}

internal sealed record AccountEditValues(string? Id, string Alias, string? Username, string? Password, bool Persist);

internal sealed class FakeRuntime : IPlatformRuntime
{
    public FakeRuntime(FakeAppManager? manager = null, AppSettings? settings = null)
    {
        Manager = manager ?? new FakeAppManager();
        SettingsStore = new InMemorySettingsStore(settings);
        Accounts = new AccountService(SettingsStore, new FakeCredentialStore(), new FakeCredentialStore());
    }

    public IAppManager Manager { get; }

    public AppProfile Profile { get; } = AppProfile.Default;

    public IAppSettingsStore SettingsStore { get; }

    public IAccountService Accounts { get; }

    public IAppLog Log { get; } = NullAppLog.Instance;

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "ibkr-manager-tests");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public List<string> Opened { get; } = [];

    public void OpenInFileExplorer(string path) => Opened.Add(path);

    public void Dispose()
    {
    }
}

internal static class States
{
    public static readonly AppInstallation Installation = new(@"C:\Program Files\IBKR Desktop\ntws.exe", @"C:\Program Files\IBKR Desktop", "0.12.0");

    public static AppState NotInstalled => AppState.Initial with { Status = AppStatus.NotInstalled };

    public static AppState Stopped => AppState.Initial with { Status = AppStatus.Stopped, Installation = Installation };

    public static AppState Running => AppState.Initial with
    {
        Status = AppStatus.Running,
        Installation = Installation,
        ProcessId = 4242,
    };

    public static readonly AccountSummary MainAccount = new("main", "Main", true, true, CredentialSource.CredentialManager);

    public static readonly AccountSummary PaperAccount = new("paper", "Paper", true, true, CredentialSource.CredentialManager);

    public static AppState WithAccounts => Stopped with
    {
        Accounts = [MainAccount, PaperAccount],
        SelectedAccountId = MainAccount.Id,
        Credentials = new ResolvedCredentials(new Secret("main-user"), new Secret("main-pass"),
            CredentialSource.CredentialManager, CredentialSource.CredentialManager,
            accountId: MainAccount.Id, accountAlias: MainAccount.Alias),
    };
}
