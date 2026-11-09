namespace IbkrManager.Core;

/// <summary>Reads the persistent per-user environment block (HKCU\Environment on Windows).</summary>
public sealed class UserEnvironmentCredentialProvider(
    Func<string> usernameVariable,
    Func<string> passwordVariable,
    IUserEnvironment environment) : ICredentialProvider
{
    public string UsernameVariable => usernameVariable();

    public string PasswordVariable => passwordVariable();

    public CredentialSource Source => CredentialSource.UserEnvironment;

    public string DisplayName => "user environment variable";

    public bool IsAvailable
    {
        get
        {
            try
            {
                return environment.IsSupported;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string? Read(CredentialKind kind) => environment.Read(kind == CredentialKind.Username ? UsernameVariable : PasswordVariable);

    string ICredentialProvider.HintFor(CredentialKind kind) =>
        $"{(kind == CredentialKind.Username ? UsernameVariable : PasswordVariable)} is not set";
}

/// <summary>Per-user persistent environment storage. Implemented per platform.</summary>
public interface IUserEnvironment
{
    bool IsSupported { get; }

    string? Read(string name);

    void Write(string name, string value);

    void Delete(string name);
}
