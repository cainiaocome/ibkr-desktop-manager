namespace IbkrManager.Core;

public enum CredentialKind
{
    Username,
    Password,
}

/// <summary>Where a credential value came from.</summary>
public enum CredentialSource
{
    None,
    CredentialManager,
    Session,
}

public static class CredentialSourceExtensions
{
    public static string ToDisplayString(this CredentialSource source) => source switch
    {
        CredentialSource.None => "not set",
        CredentialSource.CredentialManager => "stored",
        CredentialSource.Session => "this session",
        _ => source.ToString(),
    };
}
