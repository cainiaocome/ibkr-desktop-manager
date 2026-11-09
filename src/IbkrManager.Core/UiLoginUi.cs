namespace IbkrManager.Core;

/// <summary>Screen rectangle of a UI element, in physical pixels.</summary>
public readonly record struct UiBounds(int Left, int Top, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int CenterX => Left + (Width / 2);

    public int CenterY => Top + (Height / 2);

    public override string ToString() => IsEmpty ? "empty" : $"{Width}x{Height}+{Left}+{Top}";
}

/// <summary>Locations of the login controls inside the app's accessibility tree.</summary>
public sealed record UiLoginControls(
    int UsernameIndex,
    int PasswordIndex,
    bool CanSubmit,
    string Description,
    UiBounds UsernameBounds = default,
    UiBounds PasswordBounds = default,
    UiBounds SubmitBounds = default);

/// <summary>
/// The accessibility operations a login driver needs. Implemented with UI Automation on Windows;
/// faked in tests so the driver's ordering and failure handling run on every OS.
/// </summary>
public interface IUiLoginUi
{
    /// <summary>Returns the login controls, or null when no login form is on screen.</summary>
    UiLoginControls? FindLoginControls();

    /// <summary>
    /// Current text of a non-secret field, or null when it cannot be read. Drivers use this to prove
    /// that typed characters actually reached the app. Never call it for the password field.
    /// </summary>
    string? GetFieldValue(int index);

    /// <summary>Writes a field's accessibility value directly. Not all toolkits propagate it.</summary>
    bool SetField(int index, string value);

    bool Submit();

    bool WaitUntilLoginControlsGone(TimeSpan timeout, CancellationToken cancellationToken = default);
}
