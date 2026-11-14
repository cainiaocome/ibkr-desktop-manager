using IbkrManager.App.ViewModels;
using Xunit;

namespace IbkrManager.App.Tests;

public sealed class SettingsViewModelTests
{
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
            LoginDriver = "manual",
        };
        var closed = false;
        viewModel.CloseRequested += (_, saved) => closed = saved;

        await viewModel.SaveCommand.ExecuteAsync();

        var saved = runtime.SettingsStore.Load();
        Assert.Equal("manual", saved.LoginDriver);
        Assert.True(closed);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public void Existing_settings_are_loaded_into_the_form()
    {
        var runtime = new FakeRuntime(
            new FakeAppManager { State = States.Stopped },
            new AppSettings { LoginDriver = "manual" });

        var viewModel = new SettingsViewModel(runtime);

        Assert.Equal("manual", viewModel.LoginDriver);
        Assert.Contains("auto", viewModel.LoginDrivers);
    }

    [Fact]
    public async Task Saving_settings_keeps_the_accounts_and_the_selection()
    {
        var runtime = new FakeRuntime(
            new FakeAppManager { State = States.Stopped },
            new AppSettings
            {
                PersistToCredentialManager = false,
                Accounts = [new AccountProfile { Id = "main", Alias = "Main" }],
                SelectedAccountId = "main",
            });

        var viewModel = new SettingsViewModel(runtime) { LoginDriver = "manual" };
        await viewModel.SaveCommand.ExecuteAsync();

        // The settings dialog must not wipe user data it does not edit.
        var saved = runtime.SettingsStore.Load();
        Assert.False(saved.PersistToCredentialManager);
        Assert.Equal("Main", Assert.Single(saved.Accounts).Alias);
        Assert.Equal("main", saved.SelectedAccountId);
    }
}
