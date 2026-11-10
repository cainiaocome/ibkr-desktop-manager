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
    public string? UsernameValue { get; private set; }

    public string? PasswordValue { get; private set; }

    public CredentialWriteOptions? LastOptions { get; private set; }

    public int SaveCalls { get; private set; }

    public int ClearCalls { get; private set; }

    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "fake store";

    public bool IsSupported => true;

    public bool IsAvailable => true;

    public string? Read(CredentialKind kind) => kind == CredentialKind.Username ? UsernameValue : PasswordValue;

    public bool HasStoredValue(CredentialKind kind) => Read(kind) is { Length: > 0 };

    public Task SaveAsync(Secret? username, Secret? password, CredentialWriteOptions options, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        LastOptions = options;
        UsernameValue = username?.Reveal();
        PasswordValue = password?.Reveal();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ClearCalls++;
        UsernameValue = null;
        PasswordValue = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRuntime : IPlatformRuntime
{
    public FakeRuntime(FakeAppManager? manager = null, AppSettings? settings = null)
    {
        Manager = manager ?? new FakeAppManager();
        SettingsStore = new InMemorySettingsStore(settings);
        CredentialStore = new FakeCredentialStore();
        SessionStore = new FakeCredentialStore();
    }

    public IAppManager Manager { get; }

    public AppProfile Profile { get; } = AppProfile.Default;

    public IAppSettingsStore SettingsStore { get; }

    public ICredentialStore CredentialStore { get; }

    public ICredentialStore SessionStore { get; }

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
}
