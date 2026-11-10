using System.Text.Json;
using System.Text.Json.Serialization;

namespace IbkrManager.Core;

public enum WindowKind
{
    Unknown,
    Login,
    TwoFactor,
    LoggedIn,
}

/// <summary>
/// Everything the manager knows about IBKR Desktop that can change between app releases:
/// install locations, window titles and timings. Kept in JSON so an app update is a config
/// change, not a code change.
/// </summary>
public sealed record AppProfile
{
    public string DisplayName { get; init; } = "IBKR Desktop";

    public string ExecutableName { get; init; } = "ntws.exe";

    public IReadOnlyList<string> RegistryDisplayNamePatterns { get; init; } = ["IBKR Desktop"];

    public IReadOnlyList<string> FallbackInstallPaths { get; init; } = [];

    public IReadOnlyList<string> LoginWindowTitlePatterns { get; init; } = [];

    public IReadOnlyList<string> TwoFactorWindowTitlePatterns { get; init; } = [];

    public IReadOnlyList<string> LoggedInWindowTitlePatterns { get; init; } = [];

    /// <summary>Accessibility (UI Automation) ids whose presence means "login form is showing".</summary>
    public IReadOnlyList<string> LoginAutomationIdPatterns { get; init; } = [];

    /// <summary>Button labels on the login form (e.g. "Log In").</summary>
    public IReadOnlyList<string> LoginButtonNames { get; init; } = [];

    public IReadOnlyList<string> TwoFactorAutomationIdPatterns { get; init; } = [];

    public IReadOnlyList<string> LoggedInAutomationIdPatterns { get; init; } = [];

    /// <summary>
    /// Buttons that confirm "yes, close" in the dialog IBKR Desktop shows when a close request reaches
    /// a running session. Only ever pressed inside a dialog window, never in the main window.
    /// </summary>
    public IReadOnlyList<string> QuitConfirmationButtonNames { get; init; } = ["Yes", "Quit", "Exit", "Confirm", "OK"];

    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan LoginWindowTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan GracefulShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan LoginResultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int TypingDelayMilliseconds { get; init; } = 30;

    public bool SubmitAfterPassword { get; init; } = true;

    public string DownloadUrl { get; init; } = "https://www.interactivebrokers.com/en/trading/ibkr-desktop-download.php";

    /// <summary>
    /// Classifies a top-level window title. Two-factor wins over login because IBKR shows the
    /// security-code dialog with the login window still present behind it.
    /// </summary>
    public WindowKind Classify(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return WindowKind.Unknown;
        }

        if (Matches(windowTitle, TwoFactorWindowTitlePatterns))
        {
            return WindowKind.TwoFactor;
        }

        if (Matches(windowTitle, LoginWindowTitlePatterns))
        {
            return WindowKind.Login;
        }

        return Matches(windowTitle, LoggedInWindowTitlePatterns) ? WindowKind.LoggedIn : WindowKind.Unknown;
    }

    public bool MatchesRegistryName(string? displayName) => Matches(displayName, RegistryDisplayNamePatterns);

    private static bool Matches(string? value, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(value) || patterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static AppProfile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            var dto = JsonSerializer.Deserialize<ProfileDto>(json, JsonDefaults.Options)
                      ?? throw new AppProfileException("app profile JSON was empty");

            var profile = new AppProfile
            {
                DisplayName = dto.DisplayName ?? "IBKR Desktop",
                ExecutableName = dto.ExecutableName ?? "ntws.exe",
                RegistryDisplayNamePatterns = dto.RegistryDisplayNamePatterns ?? ["IBKR Desktop"],
                FallbackInstallPaths = dto.FallbackInstallPaths ?? [],
                LoginWindowTitlePatterns = dto.LoginWindowTitlePatterns ?? [],
                TwoFactorWindowTitlePatterns = dto.TwoFactorWindowTitlePatterns ?? [],
                LoggedInWindowTitlePatterns = dto.LoggedInWindowTitlePatterns ?? [],
                LoginAutomationIdPatterns = dto.LoginAutomationIdPatterns ?? [],
                LoginButtonNames = dto.LoginButtonNames ?? [],
                QuitConfirmationButtonNames = dto.QuitConfirmationButtonNames ??
                                              ["Yes", "Quit", "Exit", "Confirm", "OK"],
                TwoFactorAutomationIdPatterns = dto.TwoFactorAutomationIdPatterns ?? [],
                LoggedInAutomationIdPatterns = dto.LoggedInAutomationIdPatterns ?? [],
                LaunchTimeout = TimeSpan.FromSeconds(dto.LaunchTimeoutSeconds ?? 120),
                LoginWindowTimeout = TimeSpan.FromSeconds(dto.LoginWindowTimeoutSeconds ?? 120),
                GracefulShutdownTimeout = TimeSpan.FromSeconds(dto.GracefulShutdownTimeoutSeconds ?? 15),
                LoginResultTimeout = TimeSpan.FromSeconds(dto.LoginResultTimeoutSeconds ?? 60),
                TypingDelayMilliseconds = dto.TypingDelayMilliseconds ?? 30,
                SubmitAfterPassword = dto.SubmitAfterPassword ?? true,
                DownloadUrl = dto.DownloadUrl ?? new AppProfile().DownloadUrl,
            };

            profile.Validate();
            return profile;
        }
        catch (JsonException ex)
        {
            throw new AppProfileException($"app profile is not valid JSON: {ex.Message}", ex);
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new AppProfileException("displayName must not be empty");
        }

        if (string.IsNullOrWhiteSpace(ExecutableName) || !ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppProfileException("executableName must be a .exe file name");
        }

        if (TypingDelayMilliseconds is < 0 or > 2000)
        {
            throw new AppProfileException("typingDelayMilliseconds must be between 0 and 2000");
        }

        if (LaunchTimeout < TimeSpan.Zero || LoginWindowTimeout < TimeSpan.Zero || GracefulShutdownTimeout < TimeSpan.Zero)
        {
            throw new AppProfileException("timeouts must not be negative");
        }
    }

    public static AppProfile LoadEmbedded()
    {
        using var stream = typeof(AppProfile).Assembly.GetManifestResourceStream("IbkrManager.Core.app-profile.json")
                           ?? throw new AppProfileException("embedded app profile is missing");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    public static AppProfile Default => FromJson("{}");

    private sealed record ProfileDto
    {
        public string? DisplayName { get; init; }

        public string? ExecutableName { get; init; }

        public string[]? RegistryDisplayNamePatterns { get; init; }

        public string[]? FallbackInstallPaths { get; init; }

        public string[]? LoginWindowTitlePatterns { get; init; }

        public string[]? TwoFactorWindowTitlePatterns { get; init; }

        public string[]? LoggedInWindowTitlePatterns { get; init; }

        public string[]? LoginAutomationIdPatterns { get; init; }

        public string[]? LoginButtonNames { get; init; }

        public string[]? QuitConfirmationButtonNames { get; init; }

        public string[]? TwoFactorAutomationIdPatterns { get; init; }

        public string[]? LoggedInAutomationIdPatterns { get; init; }

        public int? LaunchTimeoutSeconds { get; init; }

        public int? LoginWindowTimeoutSeconds { get; init; }

        public int? GracefulShutdownTimeoutSeconds { get; init; }

        public int? LoginResultTimeoutSeconds { get; init; }

        public int? TypingDelayMilliseconds { get; init; }

        public bool? SubmitAfterPassword { get; init; }

        public string? DownloadUrl { get; init; }
    }
}

public sealed class AppProfileException(string message, Exception? inner = null) : Exception(message, inner);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
