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

        _credentialStore = new WindowsCredentialStore(Log);
        Accounts = new AccountService(SettingsStore, _credentialStore, _sessionStore, Log, _redactor);

        _process = new Win32ProcessController(Profile, Log);
        var uia = new UiaSession(_process, Profile, Log);
        var postedInput = new PostedWindowInput(_process);
        var driver = new SelectingLoginDriver(
            () => SettingsStore.Load().LoginDriver,
            // Blind tab-order typing is deliberately not in the production chain: with no login form
            // exposed it would type credentials into whatever happens to have focus.
            new LoginDriverChain(
                new PointerLoginDriver(
                    uia,
                    _process,
                    Log,
                    postedInput: postedInput,
                    inspector: uia),
                new UiaLoginDriver(uia, Log)),
            new ManualLoginDriver());

        var locator = new OverridingAppLocator(
            new WindowsAppLocator(Profile, new UninstallRegistry()),
            () => SettingsStore.Load().ExecutablePathOverride,
            Log);

        Manager = new AppManager(
            locator,
            _process,
            Accounts,
            driver,
            Profile,
            SystemClock.Instance,
            Log,
            closeConfirmation: new UiCloseConfirmation(_process, Profile, Log, posted: postedInput));
    }

    public IAppManager Manager { get; }

    public IAccountService Accounts { get; }

    public ICredentialStore CredentialStore => _credentialStore;

    public AppProfile Profile { get; }

    public IAppSettingsStore SettingsStore { get; }


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
