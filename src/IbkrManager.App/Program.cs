using Avalonia;

namespace IbkrManager.App;

internal static class Program
{
    private const string SingleInstanceMutexName = "IbkrDesktopManager.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = TryAcquireSingleInstance(out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    private static Mutex? TryAcquireSingleInstance(out bool isFirstInstance)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out isFirstInstance);
            return mutex;
        }
        catch (Exception)
        {
            // A missing/odd mutex (e.g. non-Windows host) must not stop the app from starting.
            isFirstInstance = true;
            return null;
        }
    }
}
