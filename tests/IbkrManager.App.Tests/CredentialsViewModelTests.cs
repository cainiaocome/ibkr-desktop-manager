using IbkrManager.App.ViewModels;
using Xunit;

namespace IbkrManager.App.Tests;

public sealed class CredentialsViewModelTests
{
    [Fact]
    public async Task Empty_username_is_rejected_without_touching_the_store()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new CredentialsViewModel(runtime) { Username = "  ", Password = "secret-pass" };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.True(viewModel.HasError);
        Assert.Contains("Username", viewModel.Error);
        Assert.Equal(0, ((FakeCredentialStore)runtime.CredentialStore).SaveCalls);
    }

    [Fact]
    public async Task Empty_password_is_rejected_when_nothing_is_stored()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new CredentialsViewModel(runtime) { Username = "alice", Password = string.Empty };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Contains("Password", viewModel.Error);
    }

    [Fact]
    public async Task Save_persists_into_the_credential_store_and_closes()
    {
        var manager = new FakeAppManager { State = States.Stopped };
        var runtime = new FakeRuntime(manager);
        var viewModel = new CredentialsViewModel(runtime)
        {
            Username = "alice",
            Password = "secret-pass",
            Remember = true,
            AlsoWriteEnvironment = false,
        };
        var closed = false;
        viewModel.CloseRequested += (_, saved) => closed = saved;

        await viewModel.SaveCommand.ExecuteAsync();

        var store = (FakeCredentialStore)runtime.CredentialStore;
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal("alice", store.UsernameValue);
        Assert.Equal("secret-pass", store.PasswordValue);
        Assert.True(store.LastOptions!.PersistInCredentialManager);
        Assert.False(store.LastOptions.WriteUserEnvironment);
        Assert.True(closed);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Save_without_remember_uses_the_session_store()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new CredentialsViewModel(runtime)
        {
            Username = "alice",
            Password = "secret-pass",
            Remember = false,
            AlsoWriteEnvironment = false,
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal("alice", ((FakeCredentialStore)runtime.SessionStore).UsernameValue);
        Assert.Equal("secret-pass", ((FakeCredentialStore)runtime.SessionStore).PasswordValue);

        // Unchecking "remember" also clears anything previously persisted.
        Assert.Equal(1, ((FakeCredentialStore)runtime.CredentialStore).SaveCalls);
        Assert.False(((FakeCredentialStore)runtime.CredentialStore).LastOptions!.PersistInCredentialManager);
    }

    [Fact]
    public async Task Save_with_environment_export_passes_the_flag_and_persists_the_setting()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new CredentialsViewModel(runtime)
        {
            Username = "alice",
            Password = "secret-pass",
            AlsoWriteEnvironment = true,
        };

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.True(((FakeCredentialStore)runtime.CredentialStore).LastOptions!.WriteUserEnvironment);
        Assert.True(runtime.SettingsStore.Load().WriteUserEnvironmentVariables);
    }

    [Fact]
    public async Task Clear_removes_stored_credentials_and_closes()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new CredentialsViewModel(runtime);
        var closed = false;
        viewModel.CloseRequested += (_, saved) => closed = saved;

        await viewModel.ClearCommand.ExecuteAsync();

        Assert.Equal(1, ((FakeCredentialStore)runtime.CredentialStore).ClearCalls);
        Assert.Equal(1, ((FakeCredentialStore)runtime.SessionStore).ClearCalls);
        Assert.True(closed);
    }

    [Fact]
    public void Existing_username_is_prefilled_but_password_never_is()
    {
        var credentials = new ResolvedCredentials(
            new Secret("alice"),
            new Secret("stored-secret"),
            CredentialSource.CredentialManager,
            CredentialSource.CredentialManager);
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped with { Credentials = credentials } });

        var viewModel = new CredentialsViewModel(runtime);

        Assert.Equal("alice", viewModel.Username);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.True(viewModel.HasStoredPassword);
        Assert.Contains("stored", viewModel.PasswordWatermark);
        Assert.Contains("credential manager", viewModel.CurrentSource);
    }

    [Fact]
    public void Cancel_closes_without_saving()
    {
        var runtime = new FakeRuntime(new FakeAppManager { State = States.Stopped });
        var viewModel = new CredentialsViewModel(runtime);
        var saved = true;
        viewModel.CloseRequested += (_, result) => saved = result;

        viewModel.CancelCommand.Execute(null);

        Assert.False(saved);
        Assert.Equal(0, ((FakeCredentialStore)runtime.CredentialStore).SaveCalls);
    }
}
