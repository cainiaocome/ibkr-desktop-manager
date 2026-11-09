namespace IbkrManager.Core;

/// <summary>Locations of the login controls inside the app's accessibility tree.</summary>
public sealed record UiLoginControls(int UsernameIndex, int PasswordIndex, bool CanSubmit, string Description);

/// <summary>
/// The accessibility operations a login driver needs. Implemented with UI Automation on Windows;
/// faked in tests so the driver's ordering and failure handling run on every OS.
/// </summary>
public interface IUiLoginUi
{
    /// <summary>Returns the login controls, or null when no login form is on screen.</summary>
    UiLoginControls? FindLoginControls();

    bool SetField(int index, string value);

    bool Submit();

    bool WaitUntilLoginControlsGone(TimeSpan timeout, CancellationToken cancellationToken = default);
}
