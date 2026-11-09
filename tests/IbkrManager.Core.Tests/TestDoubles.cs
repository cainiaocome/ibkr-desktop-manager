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

    public string? MainWindowTitle { get; set; }

    public IntPtr? MainWindowHandle { get; set; }

    public bool HasVisibleWindow { get; set; }

    public int StartCount { get; private set; }

    public int FocusCount { get; private set; }

    public int KillCount { get; private set; }

    public int WaitForExitCount { get; private set; }

    public bool CloseMainWindowResult { get; set; } = true;

    public bool ExitOnWait { get; set; } = true;

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
        if (!ExitOnWait)
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

internal sealed class FakeCredentialProvider(
    CredentialSource source,
    string? username,
    string? password,
    string displayName = "test provider") : ICredentialProvider
{
    public CredentialSource Source => source;

    public string DisplayName => displayName;

    public bool IsAvailable { get; set; } = true;

    public string? Read(CredentialKind kind) => kind == CredentialKind.Username ? username : password;

    string ICredentialProvider.HintFor(CredentialKind kind) => $"{displayName} {kind} is not set";
}

internal sealed class FakeInspector : IWindowInspector
{
    public UiObservation? Observation { get; set; }

    public int Calls { get; private set; }

    public UiObservation? Observe()
    {
        Calls++;
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
    };

    public static AppInstallation Installation => new(@"C:\Program Files\IBKR Desktop\ntws.exe", @"C:\Program Files\IBKR Desktop", "0.12.0");
}
