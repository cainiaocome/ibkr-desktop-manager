namespace IbkrManager.Core;

/// <summary>Immutable snapshot of everything the UI needs to render. Safe to log/format.</summary>
public sealed record AppState
{
    public static readonly AppState Initial = new();

    public AppStatus Status { get; init; } = AppStatus.Unknown;

    public AppInstallation? Installation { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The last Auto Login the user asked for, if any. Not a live state.</summary>
    public LoginAttempt? LastAttempt { get; init; }

    public ResolvedCredentials Credentials { get; init; } = ResolvedCredentials.Empty;

    public string? LastError { get; init; }

    public string? LastAction { get; init; }

    public bool IsBusy { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsRunning => Status.IsRunning();

    public bool HasCredentials => Credentials.IsComplete;

    public TimeSpan? Uptime(IClock clock) => StartedAt is { } started ? clock.UtcNow - started : null;

    public override string ToString() =>
        $"AppState(status={Status}, pid={ProcessId}, credentials={Credentials}, busy={IsBusy})";
}

/// <summary>
/// Outcome of one Auto Login click. Recorded so the window can report what happened, without the
/// manager watching the app afterwards.
/// </summary>
public sealed record LoginAttempt(DateTimeOffset At, bool Success, bool AwaitingTwoFactor, string Message)
{
    public override string ToString() => $"LoginAttempt(success={Success}, twoFactor={AwaitingTwoFactor}, {Message})";
}
