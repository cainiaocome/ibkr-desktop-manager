using System.Diagnostics;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Composition root for the Windows platform layer. Keeps the Avalonia project free of P/Invoke
/// details and gives the macOS port a single place to plug in a different implementation.
/// </summary>
public sealed class WindowsPlatform : IPlatformRuntime
{
    private readonly Win32ProcessController _process;
    private readonly WindowsCredentialStore _credentialStore;
    private readonly SessionCredentialStore _sessionStore = new();
    private readonly FileLog _fileLog;
    private readonly SecretRedactor _redactor = new();

    public WindowsPlatform(string dataDirectory, AppProfile? profile = null, IAppSettingsStore? settingsStore = null)
    {
        Profile = profile ?? AppProfile.LoadEmbedded();
        DataDirectory = dataDirectory;
        LogDirectory = Path.Combine(dataDirectory, "logs");
        _fileLog = new FileLog(LogDirectory);
        Log = new RedactingLog(_fileLog, _redactor);
        SettingsStore = settingsStore ?? new JsonSettingsStore(Path.Combine(dataDirectory, "settings.json"), Log);

        var environment = new WindowsUserEnvironment();
        _credentialStore = new WindowsCredentialStore(environment, () => SettingsStore.Load(), Log);

        var resolver = new CredentialResolver(
        [
            new ConfiguredEnvironmentCredentialProvider(
                () => (SettingsStore.Load().UsernameVariable, SettingsStore.Load().PasswordVariable)),
            new UserEnvironmentCredentialProvider(
                () => SettingsStore.Load().UsernameVariable,
                () => SettingsStore.Load().PasswordVariable,
                environment),
            new CredentialManagerCredentialProvider(_credentialStore),
            _sessionStore,
        ], _redactor);

        _process = new Win32ProcessController(Profile, Log);
        var driver = new SelectingLoginDriver(
            () => SettingsStore.Load().LoginDriver,
            new SendInputLoginDriver(_process, SystemClock.Instance, Log),
            new ManualLoginDriver());

        var locator = new OverridingAppLocator(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            () => SettingsStore.Load().ExecutablePathOverride,
            Log);

        Manager = new AppManager(locator, _process, resolver, driver, Profile, SystemClock.Instance, Log);
    }

    public IAppManager Manager { get; }

    public AppProfile Profile { get; }

    public IAppSettingsStore SettingsStore { get; }

    public ICredentialStore CredentialStore => _credentialStore;

    public ICredentialStore SessionStore => _sessionStore;

    public IAppLog Log { get; }

    public string DataDirectory { get; }

    public string LogDirectory { get; }

    public void OpenInFileExplorer(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    public void Dispose()
    {
        _process.Dispose();
        _fileLog.Dispose();
    }
}
