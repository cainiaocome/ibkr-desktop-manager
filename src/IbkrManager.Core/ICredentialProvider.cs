namespace IbkrManager.Core;

/// <summary>One link in the credential resolution chain (env, user env, credential manager, session).</summary>
public interface ICredentialProvider
{
    CredentialSource Source { get; }

    /// <summary>Human readable name used in hints, e.g. "IBKR_PASSWORD is not set".</summary>
    string DisplayName { get; }

    bool IsAvailable { get; }

    string? Read(CredentialKind kind);

    /// <summary>Hint shown when this provider has no value, e.g. "IBKR_PASSWORD is not set".</summary>
    string HintFor(CredentialKind kind) => $"{DisplayName} is not set";
}

/// <summary>Resolves the effective credential pair by walking the provider chain in order.</summary>
public interface ICredentialResolver
{
    ResolvedCredentials Resolve();
}

public sealed class CredentialResolver(IEnumerable<ICredentialProvider> providers, SecretRedactor? redactor = null) : ICredentialResolver
{
    private readonly List<ICredentialProvider> _providers = [.. providers];
    private readonly SecretRedactor? _redactor = redactor;

    public IReadOnlyList<ICredentialProvider> Providers => _providers;

    public ResolvedCredentials Resolve()
    {
        string? username = null;
        string? password = null;
        var usernameSource = CredentialSource.None;
        var passwordSource = CredentialSource.None;
        string? usernameHint = null;
        string? passwordHint = null;

        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable)
            {
                continue;
            }

            if (username is null)
            {
                var value = provider.Read(CredentialKind.Username);
                if (!string.IsNullOrEmpty(value))
                {
                    username = value;
                    usernameSource = provider.Source;
                }
                else
                {
                    usernameHint ??= provider.HintFor(CredentialKind.Username);
                }
            }

            if (password is null)
            {
                var value = provider.Read(CredentialKind.Password);
                if (!string.IsNullOrEmpty(value))
                {
                    password = value;
                    passwordSource = provider.Source;
                }
                else
                {
                    passwordHint ??= provider.HintFor(CredentialKind.Password);
                }
            }

            if (username is not null && password is not null)
            {
                break;
            }
        }

        _redactor?.Register(username);
        _redactor?.Register(password);

        return new ResolvedCredentials(
            Secret.From(username),
            Secret.From(password),
            usernameSource,
            passwordSource,
            usernameHint,
            passwordHint);
    }
}
