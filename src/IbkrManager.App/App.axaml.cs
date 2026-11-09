using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using IbkrManager.App.ViewModels;
using IbkrManager.App.Views;
using IbkrManager.Platform.Windows;

namespace IbkrManager.App;

public partial class App : Application
{
    private IPlatformRuntime? _runtime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!OperatingSystem.IsWindows())
            {
                desktop.MainWindow = new UnsupportedPlatformWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IbkrManager");

            _runtime = new WindowsPlatform(dataDirectory);
            _runtime.Log.Info($"Manager starting (version {typeof(App).Assembly.GetName().Version}, data {dataDirectory})");

            var viewModel = new MainWindowViewModel(_runtime.Manager, _runtime);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _runtime?.Log.Info("Manager shutting down");
        _runtime?.Dispose();
        _runtime = null;
    }
}
