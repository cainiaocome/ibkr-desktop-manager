using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// UI Automation client for IBKR Desktop. The app is a Qt 6.8/QML desktop shell (with CEF
/// widgets) whose login form is exposed with automation ids such as
/// <c>QApplication.launcherLoginWindow.TextField_QMLTYPE_74</c>, and whose window title is the
/// constant "IBKR Desktop" in every state. Accessibility is therefore the only reliable way to
/// tell the login screen from the logged-in app — and the only deterministic way to fill it.
///
/// All UIA calls are marshalled onto a dedicated STA thread: Qt's in-process provider answers
/// UIA_E_ELEMENTNOTAVAILABLE when a client calls from an MTA thread (test runners and
/// thread-pool continuations).
/// </summary>
public sealed class UiaSession(IProcessController process, AppProfile profile, IAppLog log) : IUiLoginUi, IWindowInspector
{
    private const int MaxElements = 128;
    private static readonly TimeSpan StaTimeout = TimeSpan.FromSeconds(20);

    public UiObservation? Observe()
    {
        if (!CanUse())
        {
            return null;
        }

        try
        {
            return OnSta(ObserveCore);
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            log.Warn($"UI Automation unavailable: {ex.Message}");
            return null;
        }
    }

    public UiLoginControls? FindLoginControls()
    {
        if (!CanUse())
        {
            return null;
        }

        try
        {
            return OnSta(FindLoginControlsCore);
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            log.Warn($"UI Automation login lookup failed: {ex.Message}");
            return null;
        }
    }

    public string? GetFieldValue(int index)
    {
        if (!CanUse())
        {
            return null;
        }

        try
        {
            return OnSta(() => GetFieldValueCore(index));
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            log.Warn($"UI Automation value read failed: {ex.Message}");
            return null;
        }
    }

    public bool SetField(int index, string value)
    {
        if (!CanUse())
        {
            return false;
        }

        try
        {
            return OnSta(() => SetFieldCore(index, value));
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            log.Warn($"UI Automation SetValue failed: {ex.Message}");
            return false;
        }
    }

    public bool Submit()
    {
        if (!CanUse())
        {
            return false;
        }

        try
        {
            return OnSta(SubmitCore);
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            log.Warn($"UI Automation submit failed: {ex.Message}");
            return false;
        }
    }

    public bool WaitUntilLoginControlsGone(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (FindLoginControls() is null)
            {
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            Thread.Sleep(500);
        }

        return FindLoginControls() is null;
    }

    // ---------------------------------------------------------------- core (STA only)

    private UiObservation? ObserveCore()
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        return root is null ? null : new UiObservation(root.CurrentName, ReadTree(automation, root));
    }

    private UiLoginControls? FindLoginControlsCore()
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        if (root is null)
        {
            return null;
        }

        var isLogin = Matches(root.CurrentAutomationId, profile.LoginAutomationIdPatterns)
                      || FindAll(automation, root, UIA_ControlTypeIds.UIA_ButtonControlTypeId)
                          .Any(button => Matches(button.CurrentName, profile.LoginButtonNames));
        if (!isLogin)
        {
            return null;
        }

        var edits = FindAll(automation, root, UIA_ControlTypeIds.UIA_EditControlTypeId);
        if (edits.Count < 2)
        {
            log.Warn($"Login form exposed {edits.Count} editable field(s); need at least two");
            return null;
        }

        var passwordIndex = edits.FindIndex(element => element.CurrentIsPassword != 0);
        var usernameIndex = edits.FindIndex(element => element.CurrentIsPassword == 0);
        if (usernameIndex < 0)
        {
            usernameIndex = 0;
        }

        if (passwordIndex < 0 || passwordIndex == usernameIndex)
        {
            // Qt does not always flag IsPassword; fall back to document order.
            passwordIndex = usernameIndex == 0 ? 1 : 0;
        }

        var submit = FindAll(automation, root, UIA_ControlTypeIds.UIA_ButtonControlTypeId)
            .FirstOrDefault(button => Matches(button.CurrentName, profile.LoginButtonNames));

        return new UiLoginControls(
            usernameIndex,
            passwordIndex,
            submit is not null,
            $"window='{root.CurrentName}' root='{root.CurrentAutomationId}' edits={edits.Count}" +
            $" username={ToBounds(edits[usernameIndex])} password={ToBounds(edits[passwordIndex])}" +
            $" submit={ToBounds(submit)}",
            ToBounds(edits[usernameIndex]),
            ToBounds(edits[passwordIndex]),
            ToBounds(submit));
    }

    private string? GetFieldValueCore(int index)
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        if (root is null)
        {
            return null;
        }

        var edits = FindAll(automation, root, UIA_ControlTypeIds.UIA_EditControlTypeId);
        if (index < 0 || index >= edits.Count)
        {
            return null;
        }

        return edits[index].GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern valuePattern
            ? valuePattern.CurrentValue
            : null;
    }

    private bool SetFieldCore(int index, string value)
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        if (root is null)
        {
            return false;
        }

        var edits = FindAll(automation, root, UIA_ControlTypeIds.UIA_EditControlTypeId);
        if (index < 0 || index >= edits.Count)
        {
            log.Warn($"Login field {index} does not exist ({edits.Count} editable fields)");
            return false;
        }

        if (edits[index].GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is not IUIAutomationValuePattern valuePattern)
        {
            log.Warn($"Login field {index} does not support ValuePattern");
            return false;
        }

        valuePattern.SetValue(value);
        return true;
    }

    private bool SubmitCore()
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        if (root is null)
        {
            return false;
        }

        foreach (var button in FindAll(automation, root, UIA_ControlTypeIds.UIA_ButtonControlTypeId))
        {
            if (!Matches(button.CurrentName, profile.LoginButtonNames))
            {
                continue;
            }

            if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }

        log.Info("No invokable login button; pressing Enter instead");
        process.TryFocusMainWindow();
        return Keyboard.SendKeyPress(NativeMethods.VK_RETURN);
    }

    // ---------------------------------------------------------------- helpers

    private bool CanUse() => OperatingSystem.IsWindows() && RootHandle() != IntPtr.Zero;

    private IntPtr RootHandle()
    {
        var handle = process.MainWindowHandle;
        return handle is { } value && value != IntPtr.Zero ? value : IntPtr.Zero;
    }

    private IUIAutomationElement? RootOf(CUIAutomation automation)
    {
        var handle = RootHandle();
        return handle == IntPtr.Zero ? null : automation.ElementFromHandle(handle);
    }

    private static bool Matches(string? value, IReadOnlyList<string> patterns) =>
        !string.IsNullOrEmpty(value) &&
        patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern) &&
                                value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static List<IUIAutomationElement> FindAll(CUIAutomation automation, IUIAutomationElement root, int controlType)
    {
        var condition = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, controlType);
        var found = root.FindAll(TreeScope.TreeScope_Descendants, condition);

        var result = new List<IUIAutomationElement>();
        for (var i = 0; i < found.Length && i < MaxElements; i++)
        {
            var element = found.GetElement(i);
            if (element is not null)
            {
                result.Add(element);
            }
        }

        return result;
    }

    private static List<UiElementInfo> ReadTree(CUIAutomation automation, IUIAutomationElement root)
    {
        var elements = new List<UiElementInfo>
        {
            new(
                ControlTypeName(root.CurrentControlType),
                root.CurrentName ?? string.Empty,
                root.CurrentAutomationId ?? string.Empty,
                root.CurrentIsPassword != 0),
        };

        var found = root.FindAll(TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
        for (var i = 0; i < found.Length && i < MaxElements; i++)
        {
            var element = found.GetElement(i);
            if (element is null)
            {
                continue;
            }

            elements.Add(new UiElementInfo(
                ControlTypeName(element.CurrentControlType),
                element.CurrentName ?? string.Empty,
                element.CurrentAutomationId ?? string.Empty,
                element.CurrentIsPassword != 0));
        }

        return elements;
    }

    /// <summary>Screen rectangle of an element; empty when it is offscreen or gone.</summary>
    private static UiBounds ToBounds(IUIAutomationElement? element)
    {
        if (element is null)
        {
            return default;
        }

        var rect = element.CurrentBoundingRectangle;
        return new UiBounds(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

    private static string ControlTypeName(int controlType) => controlType switch
    {
        UIA_ControlTypeIds.UIA_EditControlTypeId => "Edit",
        UIA_ControlTypeIds.UIA_ButtonControlTypeId => "Button",
        UIA_ControlTypeIds.UIA_WindowControlTypeId => "Window",
        UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => "ComboBox",
        UIA_ControlTypeIds.UIA_GroupControlTypeId => "Group",
        UIA_ControlTypeIds.UIA_PaneControlTypeId => "Pane",
        _ => $"Control{controlType}",
    };

    private static bool IsUiaFailure(Exception ex) =>
        ex is COMException or InvalidCastException or PlatformNotSupportedException or DllNotFoundException or TimeoutException;

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
            Name = "ibkr-uia",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(StaTimeout))
        {
            throw new TimeoutException("UI Automation call did not return in time");
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
