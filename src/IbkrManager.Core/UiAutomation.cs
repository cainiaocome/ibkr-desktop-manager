namespace IbkrManager.Core;

/// <summary>One accessibility node, reduced to what classification needs.</summary>
public sealed record UiElementInfo(string ControlType, string Name, string AutomationId, bool IsPassword);

/// <summary>
/// A snapshot of the app's accessibility tree. The window title alone cannot identify the login
/// screen: IBKR Desktop's Qt/QML window is titled "IBKR Desktop" in every state, so the manager
/// classifies by the QML automation ids and button labels inside it.
/// </summary>
public sealed record UiObservation(string? WindowTitle, IReadOnlyList<UiElementInfo> Elements)
{
    public bool IsEmpty => Elements.Count == 0;

    /// <summary>
    /// A window that has not finished loading exposes a single container and no content; such a
    /// snapshot must not be used to conclude "logged in".
    /// </summary>
    public bool IsMeaningful => Elements.Count >= 3;

    public bool HasAutomationId(IReadOnlyList<string> patterns) =>
        Elements.Any(element => Contains(element.AutomationId, patterns));

    public bool HasButtonNamed(IReadOnlyList<string> names) =>
        Elements.Any(element => IsButton(element.ControlType) && Contains(element.Name, names));

    public bool HasControlNamed(IReadOnlyList<string> names) =>
        Elements.Any(element => Contains(element.Name, names));

    public int CountOf(string controlType) =>
        Elements.Count(element => string.Equals(element.ControlType, controlType, StringComparison.OrdinalIgnoreCase));

    public string Describe(int max = 6) =>
        string.Join(" | ", Elements.Take(max).Select(e => $"{e.ControlType}:{e.Name}:{e.AutomationId}"));

    private static bool IsButton(string controlType) =>
        controlType.Contains("Button", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrEmpty(value) || patterns.Count == 0)
        {
            return false;
        }

        return patterns.Any(pattern =>
            !string.IsNullOrWhiteSpace(pattern) &&
            value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Reads the accessibility tree of the running app. Returns null when unavailable.</summary>
public interface IWindowInspector
{
    UiObservation? Observe();
}

public sealed class NullWindowInspector : IWindowInspector
{
    public static readonly NullWindowInspector Instance = new();

    public UiObservation? Observe() => null;
}

public static class UiClassifier
{
    /// <summary>
    /// Combines the accessibility snapshot (accurate, survives the constant title) with the title
    /// patterns (a cheap fallback when UI Automation is unavailable).
    /// </summary>
    public static WindowKind Classify(AppProfile profile, string? windowTitle, UiObservation? observation)
    {
        if (observation is { IsEmpty: false })
        {
            if (observation.HasAutomationId(profile.TwoFactorAutomationIdPatterns))
            {
                return WindowKind.TwoFactor;
            }

            if (observation.HasAutomationId(profile.LoginAutomationIdPatterns) ||
                observation.HasButtonNamed(profile.LoginButtonNames))
            {
                return WindowKind.Login;
            }

            if (observation.HasAutomationId(profile.LoggedInAutomationIdPatterns))
            {
                return WindowKind.LoggedIn;
            }

            // A meaningful snapshot without login markers means the app has moved past the login
            // form (2FA prompt or main view). A half-built tree falls through to the title.
            if (observation.IsMeaningful &&
                (profile.LoginAutomationIdPatterns.Count > 0 || profile.LoginButtonNames.Count > 0))
            {
                return WindowKind.LoggedIn;
            }
        }

        return profile.Classify(windowTitle);
    }
}
