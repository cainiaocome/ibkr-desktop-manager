namespace IbkrManager.Core;

/// <summary>Persistent secret storage plus the optional environment-variable export.</summary>
public interface ICredentialStore : ICredentialProvider
{
    /// <summary>True when this platform/store can persist credentials at all.</summary>
    bool IsSupported { get; }

    /// <summary>True when the value is currently stored (not just readable).</summary>
    bool HasStoredValue(CredentialKind kind);

    Task SaveAsync(Secret? username, Secret? password, CredentialWriteOptions options, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record CredentialWriteOptions(
    bool PersistInCredentialManager = true,
    bool WriteUserEnvironment = false)
{
    public static readonly CredentialWriteOptions SessionOnly = new(false, false);

    public static readonly CredentialWriteOptions Default = new(true, false);
}

/// <summary>A store that only remembers values for the lifetime of the process.</summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private string? _username;
    private string? _password;

    public CredentialSource Source => CredentialSource.Session;

    public string DisplayName => "credential entered in the manager";

    public bool IsSupported => true;

    public bool IsAvailable => true;

    public string? Read(CredentialKind kind) => kind == CredentialKind.Username ? _username : _password;

    public bool HasStoredValue(CredentialKind kind) => Read(kind) is { Length: > 0 };

    public Task SaveAsync(Secret? username, Secret? password, CredentialWriteOptions options, CancellationToken cancellationToken = default)
    {
        _username = username?.Reveal();
        _password = password?.Reveal();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _username = null;
        _password = null;
        return Task.CompletedTask;
    }
}
