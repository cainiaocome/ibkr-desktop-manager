using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Thin UI Automation client over <c>UIAutomationCore</c>. IBKR Desktop is a Qt/QML app whose
/// login form is exposed with automation ids such as
/// <c>QApplication.launcherLoginWindow.TextField_QMLTYPE_74</c>, and whose window title never
/// changes, so accessibility is the only reliable way to tell login from logged-in.
///
/// A <c>CUIAutomation</c> instance is created per operation: it is cheap, and it avoids COM
/// apartment affinity problems when the manager calls from the UI thread and from thread-pool
/// continuations.
/// </summary>
public sealed class UiaSession(IProcessController process, AppProfile profile, IAppLog log) : IUiLoginUi, IWindowInspector
{
    private const int EditControlType = UIA_ControlTypeIds.UIA_EditControlTypeId;
    private const int ButtonControlType = UIA_ControlTypeIds.UIA_ButtonControlTypeId;
    private const int ValuePatternId = UIA_PatternIds.UIA_ValuePatternId;
    private const int InvokePatternId = UIA_PatternIds.UIA_InvokePatternId;
    private const int MaxElements = 128;

    public UiObservation? Observe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var window = process.MainWindowHandle;
        if (window is null || window.Value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var automation = new CUIAutomation();
            var root = automation.ElementFromHandle(window.Value);
            if (root is null)
            {
                return null;
            }

            var elements = ReadTree(root);
            return new UiObservation(root.CurrentName, elements);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException or DllNotFoundException)
        {
            log.Warn($"UI Automation unavailable: {ex.Message}");
            return null;
        }
    }

    public UiLoginControls? FindLoginControls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var window = process.MainWindowHandle;
        if (window is null || window.Value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var automation = new CUIAutomation();
            var root = automation.ElementFromHandle(window.Value);
            if (root is null || !LooksLikeLogin(root))
            {
                return null;
            }

            var edits = FindAll(root, EditControlType);
            if (edits.Count == 0)
            {
                return null;
            }

            var passwordIndex = edits.FindIndex(element => element.CurrentIsPassword != 0);
            var usernameIndex = edits.FindIndex(element => element.CurrentIsPassword == 0);
            if (usernameIndex < 0)
            {
                usernameIndex = 0;
            }

            if (passwordIndex < 0)
            {
                passwordIndex = usernameIndex == 0 ? Math.Min(1, edits.Count - 1) : 0;
            }

            if (passwordIndex == usernameIndex)
            {
                log.Warn("UI Automation exposed only one editable field on the login form");
                return null;
            }

            var canSubmit = FindAll(root, ButtonControlType)
                .Any(button => Matches(button.CurrentName, profile.LoginButtonNames));

            return new UiLoginControls(
                usernameIndex,
                passwordIndex,
                canSubmit,
                $"window='{root.CurrentName}' root='{root.CurrentAutomationId}' fields={edits.Count}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException or DllNotFoundException)
        {
            log.Warn($"UI Automation login lookup failed: {ex.Message}");
            return null;
        }
    }

    public bool SetField(int index, string value)
    {
        try
        {
            var automation = new CUIAutomation();
            var root = WindowRoot(automation);
            if (root is null)
            {
                return false;
            }

            var edits = FindAll(root, EditControlType);
            if (index < 0 || index >= edits.Count)
            {
                log.Warn($"Login field {index} does not exist ({edits.Count} editable fields)");
                return false;
            }

            if (edits[index].GetCurrentPattern(ValuePatternId) is not IUIAutomationValuePattern valuePattern)
            {
                log.Warn($"Login field {index} does not support ValuePattern");
                return false;
            }

            valuePattern.SetValue(value);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException or DllNotFoundException)
        {
            log.Warn($"UI Automation SetValue failed: {ex.Message}");
            return false;
        }
    }

    public bool Submit()
    {
        try
        {
            var automation = new CUIAutomation();
            var root = WindowRoot(automation);
            if (root is null)
            {
                return false;
            }

            foreach (var button in FindAll(root, ButtonControlType))
            {
                if (!Matches(button.CurrentName, profile.LoginButtonNames))
                {
                    continue;
                }

                if (button.GetCurrentPattern(InvokePatternId) is IUIAutomationInvokePattern invoke)
                {
                    invoke.Invoke();
                    return true;
                }
            }

            // No invokable button: fall back to pressing Enter in the focused field.
            log.Info("No invokable login button; pressing Enter instead");
            process.TryFocusMainWindow();
            return Keyboard.SendKeyPress(NativeMethods.VK_RETURN);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException or DllNotFoundException)
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

    private IUIAutomationElement? WindowRoot(CUIAutomation automation)
    {
        var window = process.MainWindowHandle;
        return window is null || window.Value == IntPtr.Zero ? null : automation.ElementFromHandle(window.Value);
    }

    private bool LooksLikeLogin(IUIAutomationElement root)
    {
        if (Matches(root.CurrentAutomationId, profile.LoginAutomationIdPatterns))
        {
            return true;
        }

        return FindAll(root, ButtonControlType).Any(button => Matches(button.CurrentName, profile.LoginButtonNames));
    }

    private static bool Matches(string? value, IReadOnlyList<string> patterns) =>
        !string.IsNullOrEmpty(value) &&
        patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern) &&
                                value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static List<IUIAutomationElement> FindAll(IUIAutomationElement root, int controlType)
    {
        var automation = new CUIAutomation();
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

    private List<UiElementInfo> ReadTree(IUIAutomationElement root)
    {
        var elements = new List<UiElementInfo>
        {
            new(ControlTypeName(root.CurrentControlType), root.CurrentName ?? string.Empty, root.CurrentAutomationId ?? string.Empty, false),
        };

        var automation = new CUIAutomation();
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

    private static string ControlTypeName(int controlType) => controlType switch
    {
        EditControlType => "Edit",
        ButtonControlType => "Button",
        50032 => "Window",
        50003 => "ComboBox",
        50026 => "Group",
        _ => $"Control{controlType}",
    };
}
