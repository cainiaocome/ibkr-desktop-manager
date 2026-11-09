namespace IbkrManager.Core;

/// <summary>
/// What the manager knows about the app. Deliberately limited to observable facts — whether the
/// executable is installed and whether its process is running. The manager does not track the app's
/// UI state (login screen, 2FA, logged in): that would require it to inspect another process's
/// window on every tick, which is expensive for the app and adds nothing it can act on.
/// </summary>
public enum AppStatus
{
    Unknown,
    NotInstalled,
    Stopped,
    Running,
    Stopping,
    Failed,
}

public static class AppStatusExtensions
{
    public static bool IsRunning(this AppStatus status) => status is AppStatus.Running or AppStatus.Stopping;

    public static string ToDisplayString(this AppStatus status) => status switch
    {
        AppStatus.Unknown => "Checking…",
        AppStatus.NotInstalled => "Not installed",
        AppStatus.Stopped => "Not running",
        AppStatus.Running => "Running",
        AppStatus.Stopping => "Stopping…",
        AppStatus.Failed => "Error",
        _ => status.ToString(),
    };
}
