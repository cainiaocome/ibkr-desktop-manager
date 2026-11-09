using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace IbkrManager.App.Views;

/// <summary>
/// Shown when the manager is started on a platform it cannot manage yet. Keeps the process from
/// crashing on a Windows-only code path and tells the user (or the porting developer) why.
/// </summary>
public sealed class UnsupportedPlatformWindow : Window
{
    public UnsupportedPlatformWindow()
    {
        Title = "IBKR Desktop Manager";
        Width = 520;
        Height = 220;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Windows is required",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = "This build manages the IBKR Desktop app on Windows only. "
                           + "A macOS implementation is planned; the UI and core are already platform-neutral.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"{Environment.OSVersion} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
            },
        };
    }
}
