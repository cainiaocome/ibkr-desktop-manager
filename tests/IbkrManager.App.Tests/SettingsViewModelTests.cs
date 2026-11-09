using IbkrManager.App.ViewModels;
using Xunit;

namespace IbkrManager.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Duplicate_variable_names_are_rejected()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new SettingsViewModel(runtime)
        {
            UsernameVariable = "SAME",
            PasswordVariable = "same",
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Contains("different", viewModel.Error);
    }

    [Fact]
    public async Task Missing_executable_is_rejected()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new SettingsViewModel(runtime)
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ntws.exe"),
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Contains("Executable not found", viewModel.Error);
    }

    [Fact]
    public async Task Valid_settings_are_saved_and_the_dialog_closes()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new SettingsViewModel(runtime)
        {
            UsernameVariable = "MY_USER",
            PasswordVariable = "MY_PASS",
            LoginDriver = "manual",
        };
        var closed = false;
        viewModel.CloseRequested += (_, saved) => closed = saved;

        await viewModel.SaveCommand.ExecuteAsync();

        var saved = runtime.SettingsStore.Load();
        Assert.Equal("MY_USER", saved.UsernameVariable);
        Assert.Equal("MY_PASS", saved.PasswordVariable);
        Assert.Equal("manual", saved.LoginDriver);
        Assert.True(closed);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public void Existing_settings_are_loaded_into_the_form()
    {
        var runtime = new FakeRuntime(
            new FakeAppManager { State = States.Stopped },
            new AppSettings { UsernameVariable = "U", PasswordVariable = "P", LoginDriver = "manual" });

        var viewModel = new SettingsViewModel(runtime);

        Assert.Equal("U", viewModel.UsernameVariable);
        Assert.Equal("manual", viewModel.LoginDriver);
        Assert.Contains("sendinput", viewModel.LoginDrivers);
    }

    [Fact]
    public async Task Saving_a_driver_keeps_the_credential_preferences()
    {
        var runtime = new FakeRuntime(
            new FakeAppManager { State = States.Stopped },
            new AppSettings { PersistToCredentialManager = false, WriteUserEnvironmentVariables = true });

        var viewModel = new SettingsViewModel(runtime) { LoginDriver = "manual" };
        await viewModel.SaveCommand.ExecuteAsync();

        var saved = runtime.SettingsStore.Load();
        Assert.False(saved.PersistToCredentialManager);
        Assert.True(saved.WriteUserEnvironmentVariables);
    }
}
