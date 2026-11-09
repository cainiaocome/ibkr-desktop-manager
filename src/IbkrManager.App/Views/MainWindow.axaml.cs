using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using IbkrManager.App.ViewModels;

namespace IbkrManager.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://IbkrDesktopManager/Assets/app.ico")));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshAsync();
            }
        };
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.CredentialsRequested += OnCredentialsRequested;
        viewModel.SettingsRequested += OnSettingsRequested;

        await viewModel.InitializeAsync();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private async void OnCredentialsRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new CredentialsWindow
        {
            DataContext = new CredentialsViewModel(viewModel.Runtime),
        };

        await dialog.ShowDialog(this);
        await viewModel.RefreshAsync();
    }

    private async void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new SettingsWindow
        {
            DataContext = new SettingsViewModel(viewModel.Runtime),
        };

        await dialog.ShowDialog(this);
        await viewModel.RefreshAsync();
    }
}
