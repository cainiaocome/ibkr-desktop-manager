namespace IbkrManager.Platform.Windows;

/// <summary>
/// Keyboard injection seam. The production implementation is <see cref="SendInputKeyboard"/>; tests
/// replace it to assert the exact keystroke sequence without needing an interactive desktop.
/// </summary>
public interface IKeyboardInput
{
    /// <summary>Last Win32 error from a failed injection (0 when the last call succeeded).</summary>
    int LastError { get; }

    bool SendText(string text);

    bool SendKeyPress(ushort key);

    bool SendSelectAll();
}

/// <summary>Real injection through <c>user32!SendInput</c> (layout independent, Unicode).</summary>
public sealed class SendInputKeyboard : IKeyboardInput
{
    public int LastError => Keyboard.LastError;

    public bool SendText(string text) => Keyboard.SendText(text);

    public bool SendKeyPress(ushort key) => Keyboard.SendKeyPress(key);

    public bool SendSelectAll() => Keyboard.SendSelectAll();
}
