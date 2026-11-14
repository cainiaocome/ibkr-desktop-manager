namespace IbkrManager.Platform.Windows;

/// <summary>
/// Decides which windows may be examined for a quit confirmation. Kept separate from the UI Automation
/// code so the rule is unit-testable off Windows.
/// </summary>
public static class QuitConfirmationWindows
{
    /// <summary>
    /// A confirmation must be a window that did not exist when the close was requested, and it must not
    /// be the main window: a "Confirm"/"OK" that was already on screen belongs to the trading session
    /// (an order ticket, an alert) and pressing it would approve something the user never asked for.
    /// </summary>
    public static IReadOnlyList<IntPtr> Select(
        IntPtr mainWindow,
        IReadOnlyList<IntPtr> windowsBeforeRequest,
        IReadOnlyList<IntPtr> windowsNow)
    {
        if (windowsNow.Count == 0)
        {
            return [];
        }

        var known = new HashSet<IntPtr>(windowsBeforeRequest);
        return windowsNow
            .Where(window => window != IntPtr.Zero && window != mainWindow && !known.Contains(window))
            .ToList();
    }
}
