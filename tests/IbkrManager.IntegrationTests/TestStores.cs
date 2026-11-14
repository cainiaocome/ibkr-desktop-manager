namespace IbkrManager.IntegrationTests;

/// <summary>
/// Credential store with no Win32 behind it: the tests here must never touch the runner's real
/// Windows Credential Manager (or a developer's).
/// </summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, (string? Username, string? Password)> _values = [];

    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "test store";

    public bool IsSupported => true;

    public string? Read(string accountId, CredentialKind kind) =>
        _values.TryGetValue(accountId, out var value)
            ? kind == CredentialKind.Username ? value.Username : value.Password
            : null;

    public bool HasStoredValue(string accountId, CredentialKind kind) => Read(accountId, kind) is { Length: > 0 };

    public string? ReadLegacy(CredentialKind kind) => null;

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
