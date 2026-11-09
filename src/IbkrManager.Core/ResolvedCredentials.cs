namespace IbkrManager.Core;

/// <summary>
/// The effective credential pair plus where each half came from. Holds <see cref="Secret"/>
/// instances, so it is safe to keep in state and pass to the UI.
/// </summary>
public sealed class ResolvedCredentials
{
    public static readonly ResolvedCredentials Empty = new(null, null, CredentialSource.None, CredentialSource.None);

    public ResolvedCredentials(
        Secret? username,
        Secret? password,
        CredentialSource usernameSource,
        CredentialSource passwordSource,
        string? usernameHint = null,
        string? passwordHint = null)
    {
        Username = username;
        Password = password;
        UsernameSource = usernameSource;
        PasswordSource = passwordSource;
        UsernameHint = usernameHint;
        PasswordHint = passwordHint;
    }

    public Secret? Username { get; }

    public Secret? Password { get; }

    public CredentialSource UsernameSource { get; }

    public CredentialSource PasswordSource { get; }

    /// <summary>Actionable hint for the UI, e.g. "IBKR_USERNAME is not set".</summary>
    public string? UsernameHint { get; }

    public string? PasswordHint { get; }

    public bool HasUsername => Username is { IsEmpty: false };

    public bool HasPassword => Password is { IsEmpty: false };

    public bool IsComplete => HasUsername && HasPassword;

    public string MissingDescription =>
        !HasUsername && !HasPassword
            ? "username and password are not set"
            : !HasUsername
                ? "username is not set"
                : !HasPassword
                    ? "password is not set"
                    : string.Empty;

    public string SourcesDescription =>
        $"username: {UsernameSource.ToDisplayString()} · password: {PasswordSource.ToDisplayString()}";

    public override string ToString() =>
        $"ResolvedCredentials(username={UsernameSource.ToDisplayString()}, password={PasswordSource.ToDisplayString()})";
}
