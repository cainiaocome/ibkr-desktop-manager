using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

/// <summary>
/// The rule that keeps Quit from pressing a button in a window that was already on screen. Pure logic,
/// so it runs on every OS.
/// </summary>
public sealed class QuitConfirmationWindowsTests
{
    private static readonly IntPtr Main = (IntPtr)1;
    private static readonly IntPtr OldDialog = (IntPtr)2;
    private static readonly IntPtr NewDialog = (IntPtr)3;

    [Fact]
    public void Only_windows_that_appeared_after_the_close_request_are_candidates()
    {
        var selected = QuitConfirmationWindows.Select(
            Main,
            windowsBeforeRequest: [Main, OldDialog],
            windowsNow: [Main, OldDialog, NewDialog]);

        Assert.Equal([NewDialog], selected);
    }

    [Fact]
    public void The_main_window_is_never_a_candidate_even_if_it_is_new_to_the_snapshot()
    {
        // A stale snapshot (e.g. the window was recreated) must not make the trading window a target.
        var selected = QuitConfirmationWindows.Select(
            Main,
            windowsBeforeRequest: [],
            windowsNow: [Main, NewDialog]);

        Assert.Equal([NewDialog], selected);
    }

    [Fact]
    public void An_order_ticket_that_was_already_open_is_not_a_quit_confirmation()
    {
        var orderTicket = (IntPtr)7;

        var selected = QuitConfirmationWindows.Select(
            Main,
            windowsBeforeRequest: [Main, orderTicket],
            windowsNow: [Main, orderTicket]);

        Assert.Empty(selected);
    }

    [Fact]
    public void A_zero_handle_is_ignored()
    {
        var selected = QuitConfirmationWindows.Select(Main, [], [IntPtr.Zero, NewDialog]);

        Assert.Equal([NewDialog], selected);
    }
}
