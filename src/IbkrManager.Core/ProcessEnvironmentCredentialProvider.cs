namespace IbkrManager.Core;

/// <summary>Reads the process environment (what CI, a shell or a launcher would set).</summary>
public sealed class ProcessEnvironmentCredentialProvider : ICredentialProvider
{
    private readonly Func<string, string?> _read;

    public ProcessEnvironmentCredentialProvider(string usernameVariable, string passwordVariable)
        : this(usernameVariable, passwordVariable, Environment.GetEnvironmentVariable)
    {
    }

    public ProcessEnvironmentCredentialProvider(
        string usernameVariable,
        string passwordVariable,
        Func<string, string?> read)
    {
        UsernameVariable = string.IsNullOrWhiteSpace(usernameVariable) ? "IBKR_USERNAME" : usernameVariable;
        PasswordVariable = string.IsNullOrWhiteSpace(passwordVariable) ? "IBKR_PASSWORD" : passwordVariable;
        _read = read;
    }

    public string UsernameVariable { get; }

    public string PasswordVariable { get; }

    public CredentialSource Source => CredentialSource.ProcessEnvironment;

    public string DisplayName => "environment variable";

    public bool IsAvailable => true;

    public string? Read(CredentialKind kind) => _read(kind == CredentialKind.Username ? UsernameVariable : PasswordVariable);

    public string VariableFor(CredentialKind kind) => kind == CredentialKind.Username ? UsernameVariable : PasswordVariable;

    string ICredentialProvider.HintFor(CredentialKind kind) => $"{VariableFor(kind)} is not set";
}
