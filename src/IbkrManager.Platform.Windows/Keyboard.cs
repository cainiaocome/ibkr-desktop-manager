using System.Runtime.InteropServices;

namespace IbkrManager.Platform.Windows;

/// <summary>Unicode keyboard injection; layout independent and no clipboard involvement.</summary>
internal static class Keyboard
{
    public static bool SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var inputs = new NativeMethods.INPUT[text.Length * 2];
        var index = 0;
        foreach (var character in text)
        {
            inputs[index++] = UnicodeInput(character, keyUp: false);
            inputs[index++] = UnicodeInput(character, keyUp: true);
        }

        return Send(inputs);
    }

    public static bool SendVirtualKey(ushort key, bool keyUp)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = key,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        };

        return Send([input]);
    }

    public static bool SendKeyPress(ushort key) => SendVirtualKey(key, keyUp: false) && SendVirtualKey(key, keyUp: true);

    public static bool SendSelectAll()
    {
        var down = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL },
        };
        var a = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_A },
        };
        var aUp = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_A, dwFlags = NativeMethods.KEYEVENTF_KEYUP },
        };
        var up = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP },
        };

        return Send([down, a, aUp, up]);
    }

    private static NativeMethods.INPUT UnicodeInput(char character, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        ki = new NativeMethods.KEYBDINPUT
        {
            wScan = character,
            dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
        },
    };

    private static bool Send(NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length;
    }
}
