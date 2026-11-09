using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using IbkrManager.App.ViewModels;
using IbkrManager.App.Views;
using Xunit;

namespace IbkrManager.App.Tests;

/// <summary>
/// Renders the real window in a headless Skia surface. Catches XAML/DataContext/theme breakage
/// that unit-testing the view model alone cannot see.
/// </summary>
public sealed class MainWindowRenderTests
{
    [AvaloniaFact]
    public void Main_window_renders_the_status_rows_and_buttons()
    {
        var manager = new FakeAppManager
        {
            State = new AppState
            {
                Status = AppStatus.Running,
                Installation = States.Installation,
                ProcessId = 4242,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-12),
                LastAttempt = new LoginAttempt(
                    DateTimeOffset.UtcNow,
                    Success: true,
                    AwaitingTwoFactor: true,
                    "Credentials submitted — enter the 2FA code in the app to finish logging in"),
                LastAction = "Autologin typed username, waiting for password field",
                Credentials = new ResolvedCredentials(
                    new Secret("alice"),
                    new Secret("secret-pass"),
                    CredentialSource.ProcessEnvironment,
                    CredentialSource.ProcessEnvironment),
            },
        };

        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(manager, new FakeRuntime(manager)),
        };

        window.Show();
        window.UpdateLayout();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(780, frame!.PixelSize.Width);
        Assert.Equal(420, frame.PixelSize.Height);

        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, "main-window.png"));

        window.Close();
    }

    [AvaloniaFact]
    public void Settings_dialog_renders()
    {
        var manager = new FakeAppManager { State = States.Stopped };
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new FakeRuntime(manager)),
        };

        window.Show();
        window.UpdateLayout();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, "settings-window.png"));

        window.Close();
    }

    [AvaloniaFact]
    public void Credentials_dialog_renders()
    {
        var manager = new FakeAppManager { State = States.Stopped };
        var window = new CredentialsWindow
        {
            DataContext = new CredentialsViewModel(new FakeRuntime(manager)),
        };

        window.Show();
        window.UpdateLayout();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, "credentials-window.png"));

        window.Close();
    }
}
