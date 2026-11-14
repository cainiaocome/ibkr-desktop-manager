namespace IbkrManager.Core.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeLocator(AppInstallation? installation) : IAppLocator
{
    public AppInstallation? Installation { get; set; } = installation;

    public AppInstallation? Locate() => Installation;
}

internal sealed class FakeProcessController(IClock clock) : IProcessController
{
    public bool IsRunning { get; set; }

    public int? ProcessId { get; set; } = 4242;

    public DateTimeOffset? StartedAt { get; set; }

    private string? _mainWindowTitle;

    public string? MainWindowTitle
    {
        get
        {
            TitleReads++;
            return _mainWindowTitle;
        }

        set => _mainWindowTitle = value;
    }

    /// <summary>How often the manager asked for the window title (it should not, while idle).</summary>
    public int TitleReads { get; private set; }

    public IntPtr? MainWindowHandle { get; set; }

    public bool HasVisibleWindow { get; set; }

    public int StartCount { get; private set; }

    public int FocusCount { get; private set; }

    public int KillCount { get; private set; }

    public int WaitForExitCount { get; private set; }

    public bool CloseMainWindowResult { get; set; } = true;

    public bool ExitOnWait { get; set; } = true;

    /// <summary>Number of WaitForExitAsync calls before the app is finally gone.</summary>
    public int ExitAfterWaits { get; set; } = 1;

    public Func<bool>? OnStart { get; set; }

    public Task StartAsync(AppInstallation installation, CancellationToken cancellationToken = default)
    {
        StartCount++;
        IsRunning = true;
        StartedAt = clock.UtcNow;
        HasVisibleWindow = OnStart?.Invoke() ?? true;
        MainWindowTitle ??= "IBKR Desktop Login";
        return Task.CompletedTask;
    }

    public bool TryFocusMainWindow()
    {
        FocusCount++;
        return true;
    }

    public Task<bool> CloseMainWindowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CloseMainWindowResult);

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        WaitForExitCount++;
        if (!ExitOnWait || WaitForExitCount < ExitAfterWaits)
        {
            return Task.FromResult(false);
        }

        IsRunning = false;
        MainWindowTitle = null;
        HasVisibleWindow = false;
        return Task.FromResult(true);
    }

    public Task KillAsync(CancellationToken cancellationToken = default)
    {
        KillCount++;
        IsRunning = false;
        MainWindowTitle = null;
        HasVisibleWindow = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeCloseConfirmationPrompt : ICloseConfirmationPrompt
{
    public CloseConfirmation Result { get; set; } = CloseConfirmation.NotShown;

    public int CallCount { get; private set; }

    public Task<CloseConfirmation> AnswerAsync(CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeLoginDriver : ILoginDriver
{
    public string Name => "fake";

    public LoginResult Result { get; set; } = LoginResult.AwaitingTwoFactor("awaiting 2fa");

    public Func<LoginResult>? OnLogin { get; set; }

    public int CallCount { get; private set; }

    public LoginRequest? LastRequest { get; private set; }

    public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return Task.FromResult(OnLogin?.Invoke() ?? Result);
    }
}

/// <summary>Credential store with no Win32 behind it: enough for account/resolution tests.</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, (string? Username, string? Password)> _values = [];

    public string? LegacyUsername { get; set; }

    public string? LegacyPassword { get; set; }

    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "in-memory store";

    public bool IsSupported { get; set; } = true;

    public string? Read(string accountId, CredentialKind kind)
    {
        if (!_values.TryGetValue(accountId, out var value))
        {
            return null;
        }

        return kind == CredentialKind.Username ? value.Username : value.Password;
    }

    public bool HasStoredValue(string accountId, CredentialKind kind) => Read(accountId, kind) is { Length: > 0 };

    public string? ReadLegacy(CredentialKind kind) =>
        kind == CredentialKind.Username ? LegacyUsername : LegacyPassword;

    public Task SaveAsync(string accountId, Secret? username, Secret? password, CancellationToken cancellationToken = default)
    {
        _values[accountId] = (username?.Reveal(), password?.Reveal());
        return Task.CompletedTask;
    }

    public Task ClearAsync(string accountId, CancellationToken cancellationToken = default)
    {
        _values.Remove(accountId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeInspector : IWindowInspector
{
    public UiObservation? Observation { get; set; }

    /// <summary>Simulates an app that answers accessibility queries slowly.</summary>
    public TimeSpan Delay { get; set; }

    public int Calls { get; private set; }

    public UiObservation? Observe()
    {
        Calls++;
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }

        return Observation;
    }
}

internal sealed class RecordingLog : IAppLog
{
    public List<string> InfoMessages { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public void Info(string message) => InfoMessages.Add(message);

    public void Warn(string message) => Warnings.Add(message);

    public void Error(string message, Exception? exception = null) => Errors.Add(message);
}

internal static class TestProfile
{
    public static AppProfile Fast => AppProfile.Default with
    {
        DisplayName = "IBKR Desktop",
        ExecutableName = "ntws.exe",
        LoginWindowTitlePatterns = ["Login"],
        TwoFactorWindowTitlePatterns = ["Security Code"],
        LoggedInWindowTitlePatterns = ["Portfolio"],
        LoginAutomationIdPatterns = ["launcherLoginWindow"],
        LoginButtonNames = ["Log In"],
    };

    public static AppInstallation Installation => new(@"C:\Program Files\IBKR Desktop\ntws.exe", @"C:\Program Files\IBKR Desktop", "0.12.0");
}
