using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using IbkrManager.App.ViewModels;
using IbkrManager.Platform.Windows;

namespace IbkrManager.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private ShutdownHook? _shutdownHook;

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

        viewModel.AccountsRequested += OnAccountsRequested;
        viewModel.SettingsRequested += OnSettingsRequested;

        // Answer Windows' session-end messages immediately, and stop our work if the session ends.
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero)
        {
            _shutdownHook = ShutdownHook.TryInstall(handle, viewModel.Runtime.Log);
            if (_shutdownHook is not null)
            {
                _shutdownHook.SessionEnded += (_, _) =>
                {
                    _timer.Stop();
                    viewModel.PrepareForShutdown();
                };
            }
        }

        await viewModel.InitializeAsync();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _shutdownHook?.Dispose();
        base.OnClosed(e);
    }

    private async void OnAccountsRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var dialog = new AccountsWindow
        {
            DataContext = new AccountsViewModel(viewModel.Runtime.Manager, viewModel.Runtime),
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
