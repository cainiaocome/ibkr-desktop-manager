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
        string? passwordHint = null,
        string? accountId = null,
        string? accountAlias = null)
    {
        Username = username;
        Password = password;
        UsernameSource = usernameSource;
        PasswordSource = passwordSource;
        UsernameHint = usernameHint;
        PasswordHint = passwordHint;
        AccountId = accountId;
        AccountAlias = accountAlias;
    }

    /// <summary>The account these values belong to (null when no account is selected).</summary>
    public string? AccountId { get; }

    public string? AccountAlias { get; }

    public Secret? Username { get; }

    public Secret? Password { get; }

    public CredentialSource UsernameSource { get; }

    public CredentialSource PasswordSource { get; }

    /// <summary>Actionable hint for the UI, e.g. "the password of this account is not set".</summary>
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

    public string AccountDescription => AccountAlias is { Length: > 0 } alias ? $"'{alias}'" : "no account";

    public override string ToString() =>
        $"ResolvedCredentials(username={UsernameSource.ToDisplayString()}, password={PasswordSource.ToDisplayString()})";
}
