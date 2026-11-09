namespace IbkrManager.Platform.Windows;

/// <summary>
/// Input delivered by posting window messages (WM_LBUTTONDOWN/WM_CHAR) instead of <c>SendInput</c>.
/// Qt processes them in its normal event loop, so QML text fields update exactly as if a person typed
/// — but unlike <c>SendInput</c> it works in a session without an input desktop (a service session, a
/// scheduled task or a CI runner) and is not blocked by UIPI when the app runs elevated.
///
/// It cannot carry modifier combinations, so callers must avoid Ctrl-based shortcuts and use
/// per-character Backspace instead.
/// </summary>
public interface IPostedInput
{
    /// <summary>Posts a left click at a screen position relative to the app's main window.</summary>
    bool Click(int screenX, int screenY);

    /// <summary>Posts one character to the focused control.</summary>
    bool Text(char character);

    /// <summary>Posts a key press (no modifiers).</summary>
    bool KeyPress(ushort virtualKey);
}
