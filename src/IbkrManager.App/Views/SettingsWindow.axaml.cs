using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using IbkrManager.App.ViewModels;

namespace IbkrManager.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://IbkrDesktopManager/Assets/app.ico")));
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.CloseRequested += (_, saved) => Close(saved);
        }
    }
}
