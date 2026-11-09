using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// UI Automation client for IBKR Desktop. The app is a Qt 6.8/QML desktop shell (with CEF widgets)
/// whose login form is exposed with automation ids such as
/// <c>QApplication.launcherLoginWindow.TextField_QMLTYPE_74</c>, and whose window title is the
/// constant "IBKR Desktop" in every state. Accessibility is therefore the only reliable way to tell
/// the login screen from the logged-in app — and the only deterministic way to fill it.
///
/// Everything here is deliberately cheap, because these calls run **inside the app's UI thread**:
/// <list type="bullet">
/// <item>the tree is walked breadth-first with a depth and node budget instead of
/// <c>FindAll(Descendants)</c>, so a logged-in window with CEF content is never traversed;</item>
/// <item>properties come from a UIA cache request, so each element costs one round trip instead of
/// four;</item>
/// <item>only a login window is walked deeply — everything else is inspected one level deep;</item>
/// <item>calls run on an STA thread (Qt's provider fails from MTA) with a short timeout, and a timed
/// out call parks the whole session for a while instead of piling up blocked threads.</item>
/// </list>
/// </summary>
public sealed class UiaSession(IProcessController process, AppProfile profile, IAppLog log) : IUiLoginUi, IWindowInspector
{
    private const int MaxElements = 150;
    private const int DeepDepth = 3;
    private const int ShallowDepth = 2;
    private static readonly TimeSpan StaTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DegradedPeriod = TimeSpan.FromSeconds(30);

    private DateTimeOffset _degradedUntil = DateTimeOffset.MinValue;

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

    public int? GetFieldLength(int index)
    {
        if (!CanUse())
        {
            return null;
        }

        try
        {
            return OnSta(() => GetFieldValueCore(index)?.Length);
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
        if (root is null)
        {
            return null;
        }

        // A login window is small; anything else (the trading app, CEF content) is inspected one
        // level deep only. That keeps a logged-in window from being traversed at all.
        var depth = IsLoginWindow(root) ? DeepDepth : ShallowDepth;
        var nodes = Walk(automation, root, depth);
        return new UiObservation(root.CachedName, nodes.Select(Describe).ToList());
    }

    private UiLoginControls? FindLoginControlsCore()
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        if (root is null)
        {
            return null;
        }

        var nodes = Walk(automation, root, IsLoginWindow(root) ? DeepDepth : ShallowDepth);
        var loginButton = nodes.FirstOrDefault(node =>
            node.Element.CachedControlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId &&
            Matches(node.Element.CachedName, profile.LoginButtonNames));

        if (!IsLoginWindow(root) && loginButton is null)
        {
            return null;
        }

        var edits = nodes
            .Where(node => node.Element.CachedControlType == UIA_ControlTypeIds.UIA_EditControlTypeId)
            .ToList();

        if (edits.Count < 2)
        {
            log.Warn($"Login form exposed {edits.Count} editable field(s); need at least two");
            return null;
        }

        var passwordIndex = edits.FindIndex(node => node.Element.CachedIsPassword != 0);
        var usernameIndex = edits.FindIndex(node => node.Element.CachedIsPassword == 0);
        if (usernameIndex < 0)
        {
            usernameIndex = 0;
        }

        if (passwordIndex < 0 || passwordIndex == usernameIndex)
        {
            // Qt does not always flag IsPassword; fall back to document order.
            passwordIndex = usernameIndex == 0 ? 1 : 0;
        }

        return new UiLoginControls(
            usernameIndex,
            passwordIndex,
            loginButton is not null,
            $"window='{root.CachedName}' root='{root.CachedAutomationId}' edits={edits.Count}" +
            $" username={ToBounds(edits[usernameIndex].Element)} password={ToBounds(edits[passwordIndex].Element)}" +
            $" submit={ToBounds(loginButton?.Element)}",
            ToBounds(edits[usernameIndex].Element),
            ToBounds(edits[passwordIndex].Element),
            ToBounds(loginButton?.Element));
    }

    private string? GetFieldValueCore(int index)
    {
        var automation = new CUIAutomation();
        var root = RootOf(automation);
        if (root is null)
        {
            return null;
        }

        var edits = Walk(automation, root, DeepDepth)
            .Where(node => node.Element.CachedControlType == UIA_ControlTypeIds.UIA_EditControlTypeId)
            .ToList();

        if (index < 0 || index >= edits.Count)
        {
            return null;
        }

        return edits[index].Element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern valuePattern
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

        var edits = Walk(automation, root, DeepDepth)
            .Where(node => node.Element.CachedControlType == UIA_ControlTypeIds.UIA_EditControlTypeId)
            .ToList();

        if (index < 0 || index >= edits.Count)
        {
            log.Warn($"Login field {index} does not exist ({edits.Count} editable fields)");
            return false;
        }

        if (edits[index].Element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is not IUIAutomationValuePattern valuePattern)
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

        var buttons = Walk(automation, root, DeepDepth)
            .Where(node => node.Element.CachedControlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId)
            .ToList();

        foreach (var button in buttons)
        {
            if (!Matches(button.Element.CachedName, profile.LoginButtonNames))
            {
                continue;
            }

            if (button.Element.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is IUIAutomationInvokePattern invoke)
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

    private bool CanUse() => OperatingSystem.IsWindows() && DateTimeOffset.UtcNow >= _degradedUntil && RootHandle() != IntPtr.Zero;

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

    private bool IsLoginWindow(IUIAutomationElement root) =>
        Matches(root.CachedAutomationId, profile.LoginAutomationIdPatterns);

    private static bool Matches(string? value, IReadOnlyList<string> patterns) =>
        !string.IsNullOrEmpty(value) &&
        patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern) &&
                                value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>Breadth-first walk with a depth and node budget; only containers are expanded.</summary>
    private static List<Node> Walk(CUIAutomation automation, IUIAutomationElement root, int maxDepth)
    {
        var cache = CreateCacheRequest(automation);
        var trueCondition = automation.CreateTrueCondition();
        var nodes = new List<Node> { new(root, 0) };
        var queue = new Queue<Node>();
        queue.Enqueue(nodes[0]);

        while (queue.Count > 0 && nodes.Count < MaxElements)
        {
            var node = queue.Dequeue();
            if (node.Depth >= maxDepth || !IsContainer(node.Element.CachedControlType))
            {
                continue;
            }

            IUIAutomationElementArray children;
            try
            {
                children = node.Element.FindAllBuildCache(TreeScope.TreeScope_Children, trueCondition, cache);
            }
            catch (COMException)
            {
                // The element went away mid-walk; keep whatever was collected.
                continue;
            }

            for (var i = 0; i < children.Length && nodes.Count < MaxElements; i++)
            {
                var child = children.GetElement(i);
                if (child is null)
                {
                    continue;
                }

                var next = new Node(child, node.Depth + 1);
                nodes.Add(next);
                queue.Enqueue(next);
            }
        }

        return nodes;
    }

    /// <summary>One round trip per element instead of one per property.</summary>
    private static IUIAutomationCacheRequest CreateCacheRequest(CUIAutomation automation)
    {
        var cache = automation.CreateCacheRequest();
        cache.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_NamePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_AutomationIdPropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_IsPasswordPropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        return cache;
    }

    private static bool IsContainer(int controlType) => controlType is
        UIA_ControlTypeIds.UIA_CustomControlTypeId or
        UIA_ControlTypeIds.UIA_GroupControlTypeId or
        UIA_ControlTypeIds.UIA_WindowControlTypeId or
        UIA_ControlTypeIds.UIA_PaneControlTypeId or
        UIA_ControlTypeIds.UIA_ListControlTypeId or
        UIA_ControlTypeIds.UIA_TableControlTypeId;

    private static UiElementInfo Describe(Node node) => new(
        ControlTypeName(node.Element.CachedControlType),
        node.Element.CachedName ?? string.Empty,
        node.Element.CachedAutomationId ?? string.Empty,
        node.Element.CachedIsPassword != 0);

    private static UiBounds ToBounds(IUIAutomationElement? element)
    {
        if (element is null)
        {
            return default;
        }

        var rect = element.CachedBoundingRectangle;
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
        UIA_ControlTypeIds.UIA_ProgressBarControlTypeId => "ProgressBar",
        _ => $"Control{controlType}",
    };

    private static bool IsUiaFailure(Exception ex) =>
        ex is COMException or InvalidCastException or PlatformNotSupportedException or DllNotFoundException or TimeoutException;

    /// <summary>
    /// Runs a UIA operation on a short-lived STA thread. A call that does not return in time parks the
    /// session: repeating it would only queue more blocked threads and more work on the app's UI thread.
    /// </summary>
    private T OnSta<T>(Func<T> action)
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
            _degradedUntil = DateTimeOffset.UtcNow + DegradedPeriod;
            throw new TimeoutException(
                $"UI Automation call did not return within {StaTimeout.TotalSeconds:0} s; pausing accessibility checks for " +
                $"{DegradedPeriod.TotalSeconds:0} s");
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    private sealed record Node(IUIAutomationElement Element, int Depth);
}
