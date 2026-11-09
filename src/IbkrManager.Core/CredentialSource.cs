namespace IbkrManager.Core;

public enum CredentialKind
{
    Username,
    Password,
}

/// <summary>Where a credential value was resolved from. Order is the resolution precedence.</summary>
public enum CredentialSource
{
    None,
    ProcessEnvironment,
    UserEnvironment,
    CredentialManager,
    Session,
}

public static class CredentialSourceExtensions
{
    public static string ToDisplayString(this CredentialSource source) => source switch
    {
        CredentialSource.None => "not set",
        CredentialSource.ProcessEnvironment => "env",
        CredentialSource.UserEnvironment => "user env",
        CredentialSource.CredentialManager => "credential manager",
        CredentialSource.Session => "this session",
        _ => source.ToString(),
    };
}
