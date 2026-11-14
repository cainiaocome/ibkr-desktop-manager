using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Answers the "really quit?" question IBKR Desktop shows when a close request reaches a running
/// session. Measured behaviour: on the login screen <c>WM_CLOSE</c> exits the app within ~1.5 s with no
/// dialog at all, so this only ever has work to do once the app is logged in.
///
/// The search is deliberately narrow. Only buttons inside a window that is *not* the app's main window
/// are pressed, because IBKR's QML dialogs are separate top-level windows (the login-failure dialog
/// proved it) while the main window contains trading controls whose names ("Close", "OK") must never
/// be pressed by accident. Buttons named "no"/"cancel" are never touched — the user asked to quit.
/// </summary>
public sealed class UiCloseConfirmation : ICloseConfirmationPrompt
{
    private const int NodeBudget = 120;
    private const int MaxDepth = 3;
    private static readonly TimeSpan StaTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessController _process;
    private readonly AppProfile _profile;
    private readonly IAppLog _log;
    private readonly IMouseInput _mouse;
    private readonly IPostedInput _posted;
    private readonly Func<bool> _isInteractive;

    public UiCloseConfirmation(
        IProcessController process,
        AppProfile profile,
        IAppLog log,
        IMouseInput? mouse = null,
        IPostedInput? posted = null,
        Func<bool>? isInteractive = null)
    {
        _process = process;
        _profile = profile;
        _log = log;
        _mouse = mouse ?? new SendInputMouse();
        _posted = posted ?? new PostedWindowInput(process);
        _isInteractive = isInteractive ?? InteractiveSession.IsInteractive;
    }

    public Task<CloseConfirmation> AnswerAsync(
        CloseConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !_process.IsRunning || _process.ProcessId is not { } processId)
        {
            return Task.FromResult(CloseConfirmation.NotShown);
        }

        Candidate? candidate;
        try
        {
            candidate = OnSta(() => FindCandidate(processId, request));
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or ArgumentException)
        {
            _log.Warn($"Could not look for a quit confirmation: {ex.Message}");
            return Task.FromResult(CloseConfirmation.CouldNotAnswer);
        }

        if (candidate is not { } found)
        {
            return Task.FromResult(CloseConfirmation.NotShown);
        }

        _log.Info($"IBKR Desktop is asking to confirm the close ('{found.Name}'); answering it");
        return Task.FromResult(Press(found, cancellationToken)
            ? CloseConfirmation.Answered
            : CloseConfirmation.CouldNotAnswer);
    }

    private bool Press(Candidate candidate, CancellationToken cancellationToken)
    {
        // Same lesson as the login form: SendInput is silently dropped in a service session and across
        // an elevation boundary, so the click is only trusted once the dialog actually goes away.
        if (_isInteractive() &&
            NativeMethods.SetForegroundWindow(candidate.Window) &&
            NativeMethods.GetForegroundWindow() == candidate.Window &&
            _mouse.Click(candidate.Bounds.CenterX, candidate.Bounds.CenterY) &&
            WaitForDialogToClose(candidate.Window, TimeSpan.FromSeconds(2), cancellationToken))
        {
            return true;
        }

        return _posted.Click(candidate.Window, candidate.Bounds.CenterX, candidate.Bounds.CenterY) &&
               WaitForDialogToClose(candidate.Window, TimeSpan.FromSeconds(5), cancellationToken);
    }

    private static bool WaitForDialogToClose(IntPtr window, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            // A cancelled wait means "unknown", not "answered": never report a confirmation we did not see.
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.IsWindow(window))
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return !NativeMethods.IsWindow(window);
    }

    // ---------------------------------------------------------------- core (STA only)

    private Candidate? FindCandidate(int processId, CloseConfirmationRequest request)
    {
        // Only windows that appeared after the close request: anything already on screen belongs to the
        // trading session and must not be touched.
        var candidates = QuitConfirmationWindows.Select(
            request.MainWindow,
            request.WindowsBeforeRequest,
            WindowLocator.FindVisibleWindows(processId));
        if (candidates.Count == 0)
        {
            return null;
        }

        var automation = new CUIAutomation();

        // A modal confirmation usually owns the foreground; try that one first.
        var foreground = NativeMethods.GetForegroundWindow();
        if (candidates.Contains(foreground) && Inspect(automation, foreground) is { } focused)
        {
            return focused;
        }

        foreach (var window in candidates)
        {
            if (window != foreground && Inspect(automation, window) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private Candidate? Inspect(CUIAutomation automation, IntPtr window)
    {
        IUIAutomationElement root;
        try
        {
            root = automation.ElementFromHandle(window);
        }
        catch (COMException)
        {
            return null;
        }

        return root is null ? null : FindConfirmButton(automation, root, window);
    }

    private Candidate? FindConfirmButton(CUIAutomation automation, IUIAutomationElement root, IntPtr window)
    {
        var pending = new Queue<(IUIAutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;

        while (pending.Count > 0 && visited++ < NodeBudget)
        {
            var (element, depth) = pending.Dequeue();
            int controlType;
            string name;
            try
            {
                controlType = element.CurrentControlType;
                name = element.CurrentName;
            }
            catch (COMException)
            {
                continue;
            }

            if (controlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId &&
                _profile.QuitConfirmationButtonNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var bounds = ToBounds(element);
                if (!bounds.IsEmpty)
                {
                    return new Candidate(window, name.Trim(), bounds);
                }
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            IUIAutomationElementArray? children;
            try
            {
                children = element.FindAll(TreeScope.TreeScope_Children, automation.CreateTrueCondition());
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                continue;
            }

            for (var i = 0; i < children.Length; i++)
            {
                var child = children.GetElement(i);
                if (child is not null)
                {
                    pending.Enqueue((child, depth + 1));
                }
            }
        }

        return null;
    }

    private static UiBounds ToBounds(IUIAutomationElement element)
    {
        try
        {
            var rect = element.CurrentBoundingRectangle;
            return rect.right > rect.left && rect.bottom > rect.top
                ? new UiBounds(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top)
                : default;
        }
        catch (COMException)
        {
            return default;
        }
    }

    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "ibkr-quit-confirmation",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(StaTimeout))
        {
            throw new TimeoutException("looking for the quit confirmation timed out");
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    private readonly record struct Candidate(IntPtr Window, string Name, UiBounds Bounds);
}
