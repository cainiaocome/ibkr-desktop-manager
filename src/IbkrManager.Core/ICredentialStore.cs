namespace IbkrManager.Core;

/// <summary>Persistent or in-memory secret storage, keyed by account id.</summary>
public interface ICredentialStore
{
    /// <summary>Where values read from this store come from (shown in the UI).</summary>
    CredentialSource Source { get; }

    string DisplayName { get; }

    /// <summary>True when this platform/store can persist credentials at all.</summary>
    bool IsSupported { get; }

    string? Read(string accountId, CredentialKind kind);

    bool HasStoredValue(string accountId, CredentialKind kind);

    /// <summary>
    /// The single un-named pair written by 0.3.x (<c>IbkrDesktopManager/username|password</c>).
    /// Read once by the migrator; never written again.
    /// </summary>
    string? ReadLegacy(CredentialKind kind);

    Task SaveAsync(string accountId, Secret? username, Secret? password, CancellationToken cancellationToken = default);

    Task ClearAsync(string accountId, CancellationToken cancellationToken = default);
}

/// <summary>A store that only remembers values for the lifetime of the process.</summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, (string? Username, string? Password)> _values = [];
    private readonly object _gate = new();

    public CredentialSource Source => CredentialSource.Session;

    public string DisplayName => "entered in the manager";

    public bool IsSupported => true;

    public string? Read(string accountId, CredentialKind kind)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(accountId, out var value))
            {
                return null;
            }

            return kind == CredentialKind.Username ? value.Username : value.Password;
        }
    }

    public bool HasStoredValue(string accountId, CredentialKind kind) => Read(accountId, kind) is { Length: > 0 };

    public string? ReadLegacy(CredentialKind kind) => null;

    public Task SaveAsync(string accountId, Secret? username, Secret? password, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _values[accountId] = (username?.Reveal(), password?.Reveal());
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(string accountId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _values.Remove(accountId);
        }

        return Task.CompletedTask;
    }
}
