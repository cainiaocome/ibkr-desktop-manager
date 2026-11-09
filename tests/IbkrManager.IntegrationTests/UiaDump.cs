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
