using Interop.UIAutomationClient;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// Reads the accessibility text of an arbitrary window handle. The production inspector is bound to
/// the app's main window, but a rejected login can show a separate dialog whose text is only visible
/// on that dialog's own window.
/// </summary>
internal static class UiaDump
{
    private const int MaxElements = 200;

    public static IReadOnlyList<string> TextsOf(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            return OnSta(() => ReadTexts(window));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return [$"(UIA failed: {ex.Message})"];
        }
    }

    /// <summary>
    /// Control types, names, automation ids and clickable rectangles of every element under a window,
    /// breadth-first and bounded. Used to identify buttons the app shows in a dialog.
    /// </summary>
    public static IReadOnlyList<string> Elements(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            return OnSta(() => ReadElements(window));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return [$"(UIA failed: {ex.Message})"];
        }
    }

    private static List<string> ReadElements(IntPtr window)
    {
        const int NodeBudget = 150;
        var automation = new CUIAutomation();
        var root = automation.ElementFromHandle(window);
        var lines = new List<string>();
        var pending = new Queue<(Interop.UIAutomationClient.IUIAutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0 && lines.Count < NodeBudget)
        {
            var (element, depth) = pending.Dequeue();
            try
            {
                var bounds = element.CurrentBoundingRectangle;
                var rectangle = bounds.right > bounds.left && bounds.bottom > bounds.top
                    ? $"{bounds.left},{bounds.top},{bounds.right - bounds.left}x{bounds.bottom - bounds.top}"
                    : "no-rect";
                lines.Add(
                    $"{new string(' ', depth * 2)}{ControlTypeName(element.CurrentControlType)}|" +
                    $"name=\"{element.CurrentName}\"|id={element.CurrentAutomationId}|{rectangle}");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                lines.Add($"{new string(' ', depth * 2)}(unreadable)");
                continue;
            }

            if (depth >= 3)
            {
                continue;
            }

            Interop.UIAutomationClient.IUIAutomationElementArray? children;
            try
            {
                children = element.FindAll(TreeScope.TreeScope_Children, automation.CreateTrueCondition());
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
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

        return lines;
    }

    private static string ControlTypeName(int controlType) => controlType switch
    {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50010 => "MenuBar",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50017 => "StatusBar",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        _ => $"ControlType({controlType})",
    };

    private static List<string> ReadTexts(IntPtr window)
    {
        var automation = new CUIAutomation();
        var root = automation.ElementFromHandle(window);
        var texts = new List<string>();

        if (!string.IsNullOrWhiteSpace(root.CurrentName))
        {
            texts.Add(root.CurrentName);
        }

        var found = root.FindAll(TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
        for (var i = 0; i < found.Length && i < MaxElements; i++)
        {
            var element = found.GetElement(i);
            if (element is null)
            {
                continue;
            }

            var name = element.CurrentName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                texts.Add(name);
            }
        }

        return texts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            Name = "ibkr-uia-dump",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("reading the accessibility tree timed out");
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
