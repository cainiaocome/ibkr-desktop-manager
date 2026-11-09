namespace IbkrManager.Platform.Windows;

/// <summary>
/// Mouse injection seam. The production implementation is <see cref="SendInputMouse"/>; tests
/// replace it to assert where the driver clicks without needing an interactive desktop.
/// </summary>
public interface IMouseInput
{
    /// <summary>Last Win32 error from a failed injection (0 when the last call succeeded).</summary>
    int LastError { get; }

    /// <summary>Moves the pointer to a screen position and clicks the left button there.</summary>
    bool Click(int x, int y);
}

/// <summary>Real clicks through <c>user32!SetCursorPos</c> + <c>SendInput</c>.</summary>
public sealed class SendInputMouse : IMouseInput
{
    public int LastError => Mouse.LastError;

    public bool Click(int x, int y) => Mouse.Click(x, y);
}
