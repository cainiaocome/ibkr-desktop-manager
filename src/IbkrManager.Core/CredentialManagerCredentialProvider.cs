namespace IbkrManager.Core;

/// <summary>Reads the managed secret store (Windows Credential Manager on Windows).</summary>
public sealed class CredentialManagerCredentialProvider(ICredentialStore store) : ICredentialProvider
{
    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "stored credential";

    public bool IsAvailable
    {
        get
        {
            try
            {
                return store.IsSupported;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string? Read(CredentialKind kind) => store.Read(kind);

    string ICredentialProvider.HintFor(CredentialKind kind) => $"stored {kind.ToString().ToLowerInvariant()} is not set";
}
