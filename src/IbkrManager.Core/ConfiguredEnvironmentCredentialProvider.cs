namespace IbkrManager.Core;

/// <summary>
/// Process-environment provider whose variable names are read from settings on every call,
/// so renaming them takes effect without a restart.
/// </summary>
public sealed class ConfiguredEnvironmentCredentialProvider(
    Func<(string Username, string Password)> variableNames,
    Func<string, string?>? read = null) : ICredentialProvider
{
    private readonly Func<string, string?> _read = read ?? Environment.GetEnvironmentVariable;

    public CredentialSource Source => CredentialSource.ProcessEnvironment;

    public string DisplayName => "environment variable";

    public bool IsAvailable => true;

    public string? Read(CredentialKind kind) => _read(NameFor(kind));

    string ICredentialProvider.HintFor(CredentialKind kind) => $"{NameFor(kind)} is not set";

    public string NameFor(CredentialKind kind)
    {
        var (username, password) = variableNames();
        return kind == CredentialKind.Username ? username : password;
    }
}
