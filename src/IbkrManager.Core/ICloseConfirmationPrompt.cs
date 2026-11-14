namespace IbkrManager.Core;

/// <summary>What the manager did about the app's own "really quit?" question.</summary>
public enum CloseConfirmation
{
    /// <summary>The app did not ask anything (it either exited or is simply slow).</summary>
    NotShown,

    /// <summary>The question was found and its confirming button was pressed.</summary>
    Answered,

    /// <summary>The app is asking, but the confirming button could not be operated.</summary>
    CouldNotAnswer,
}

/// <summary>
/// What the manager knows when it asks: the main window, and every window of the process that already
/// existed before the close request. A confirmation can only be a window that is *not* in that list,
/// which is what keeps a stray "OK"/"Confirm" in an old trading dialog from being pressed.
/// </summary>
public sealed record CloseConfirmationRequest(IntPtr MainWindow, IReadOnlyList<IntPtr> WindowsBeforeRequest);

/// <summary>
/// Answers the confirmation IBKR Desktop shows after a close request. Clicking the window's X raises
/// the same question, so an automated Quit has to be able to answer it — otherwise the only way out is
/// terminating the process, which skips the app's own cleanup.
/// </summary>
public interface ICloseConfirmationPrompt
{
    /// <summary>
    /// Looks for a pending confirmation and answers it. Runs once, inside the Quit command, so it is
    /// quick and must not throw for a missing dialog.
    /// </summary>
    Task<CloseConfirmation> AnswerAsync(CloseConfirmationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Used on platforms (and in tests) where no confirmation can be answered.</summary>
public sealed class NullCloseConfirmationPrompt : ICloseConfirmationPrompt
{
    public static readonly NullCloseConfirmationPrompt Instance = new();

    public Task<CloseConfirmation> AnswerAsync(
        CloseConfirmationRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(CloseConfirmation.NotShown);
}
