namespace IbkrManager.Core;

/// <summary>Owns the lifetime of the IBKR Desktop process (start, focus, close, kill, query).</summary>
public interface IProcessController
{
    bool IsRunning { get; }

    int? ProcessId { get; }

    DateTimeOffset? StartedAt { get; }

    string? MainWindowTitle { get; }

    /// <summary>Native handle of the main window, used to attach UI Automation.</summary>
    IntPtr? MainWindowHandle { get; }

    bool HasVisibleWindow { get; }

    /// <summary>Visible top-level windows of the app (used to spot a dialog that appeared later).</summary>
    IReadOnlyList<IntPtr> FindWindows();

    Task StartAsync(AppInstallation installation, CancellationToken cancellationToken = default);

    bool TryFocusMainWindow();

    Task<bool> CloseMainWindowAsync(CancellationToken cancellationToken = default);

    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    Task KillAsync(CancellationToken cancellationToken = default);
}
