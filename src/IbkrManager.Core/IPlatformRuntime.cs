namespace IbkrManager.Core;

/// <summary>
/// Everything the UI needs from the host platform. Windows implements this today; a macOS
/// implementation is the only new code that port would require.
/// </summary>
public interface IPlatformRuntime : IDisposable
{
    IAppManager Manager { get; }

    AppProfile Profile { get; }

    IAppSettingsStore SettingsStore { get; }

    ICredentialStore CredentialStore { get; }

    /// <summary>Lifetime-of-process credential store used when "remember" is unchecked.</summary>
    ICredentialStore SessionStore { get; }

    IAppLog Log { get; }

    string DataDirectory { get; }

    string LogDirectory { get; }

    void OpenInFileExplorer(string path);
}
