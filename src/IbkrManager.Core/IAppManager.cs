namespace IbkrManager.Core;

/// <summary>Commands and state exposed to the UI.</summary>
public interface IAppManager
{
    AppState State { get; }

    AppProfile Profile { get; }

    event EventHandler<AppState>? StateChanged;

    Task<AppState> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads process/credential state; called by the UI timer.</summary>
    Task<AppState> TickAsync(CancellationToken cancellationToken = default);

    Task<AppState> StartAsync(CancellationToken cancellationToken = default);

    Task<AppState> AutoLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes which account Auto Login uses; also the default at startup.</summary>
    Task<AppState> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default);

    Task<AppState> SaveAccountAsync(AccountEdit edit, CancellationToken cancellationToken = default);

    Task<AppState> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default);

    Task<AppState> QuitAsync(bool force = false, CancellationToken cancellationToken = default);

    Task<AppState> RestartAsync(CancellationToken cancellationToken = default);
}
