namespace IbkrManager.Core;

public enum AppStatus
{
    Unknown,
    NotInstalled,
    Stopped,
    Launching,
    AwaitingLogin,
    AwaitingTwoFactor,
    LoggedIn,
    Stopping,
    Failed,
}

public static class AppStatusExtensions
{
    public static bool IsRunning(this AppStatus status) => status is AppStatus.Launching
        or AppStatus.AwaitingLogin
        or AppStatus.AwaitingTwoFactor
        or AppStatus.LoggedIn
        or AppStatus.Stopping;

    public static string ToDisplayString(this AppStatus status) => status switch
    {
        AppStatus.Unknown => "Checking…",
        AppStatus.NotInstalled => "Not installed",
        AppStatus.Stopped => "Not running",
        AppStatus.Launching => "Starting…",
        AppStatus.AwaitingLogin => "At login screen",
        AppStatus.AwaitingTwoFactor => "Awaiting 2FA",
        AppStatus.LoggedIn => "Running",
        AppStatus.Stopping => "Stopping…",
        AppStatus.Failed => "Error",
        _ => status.ToString(),
    };
}
