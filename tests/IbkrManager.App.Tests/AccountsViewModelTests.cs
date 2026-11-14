using IbkrManager.App.ViewModels;
using Xunit;

namespace IbkrManager.App.Tests;

public sealed class AccountsViewModelTests
{
    [Fact]
    public void The_list_shows_every_account_and_marks_the_one_in_use()
    {
        var (viewModel, _) = Create(States.WithAccounts);

        Assert.Equal(["Main  ·  in use", "Paper"], viewModel.Accounts.Select(row => row.Title));
        Assert.Contains("stored", viewModel.Accounts[0].Presence);
    }

    [Fact]
    public void Choosing_a_row_prefills_the_alias_and_username_but_never_the_password()
    {
        var (viewModel, runtime) = Create(States.WithAccounts);
        Seed(runtime, "paper", "Paper", "paper-user", "paper-pass");

        viewModel.SelectedRow = viewModel.Accounts[1];

        Assert.Equal("Paper", viewModel.Alias);
        Assert.Equal("paper-user", viewModel.Username);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal("stored — type to replace", viewModel.PasswordWatermark);
        Assert.Equal("Edit “Paper”", viewModel.EditorTitle);
    }

    [Fact]
    public async Task A_new_account_needs_a_name_username_and_password()
    {
        var (viewModel, _) = Create(States.Stopped);

        await viewModel.SaveCommand.ExecuteAsync();
        Assert.Contains("name", viewModel.Error);

        viewModel.Alias = "Paper";
        await viewModel.SaveCommand.ExecuteAsync();
        Assert.Contains("username and a password", viewModel.Error);
    }

    [Fact]
    public async Task Saving_a_new_account_sends_an_edit_without_an_id()
    {
        var (viewModel, _) = Create(States.Stopped);
        viewModel.Alias = "Paper";
        viewModel.Username = "paper-user";
        viewModel.Password = "paper-pass";
        viewModel.Remember = false;

        await viewModel.SaveCommand.ExecuteAsync();

        var edit = _manager.LastEditValues!;
        Assert.Null(edit.Id);
        Assert.Equal("Paper", edit.Alias);
        Assert.Equal("paper-user", edit.Username);
        Assert.Equal("paper-pass", edit.Password);
        Assert.False(edit.Persist);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Editing_an_account_keeps_the_stored_prompt_and_sends_its_id()
    {
        var (viewModel, runtime) = Create(States.WithAccounts);
        Seed(runtime, "main", "Main", "main-user", "main-pass");

        viewModel.SelectedRow = viewModel.Accounts[0];
        viewModel.Alias = "Primary";
        viewModel.Password = "new-pass";

        await viewModel.SaveCommand.ExecuteAsync();

        var edit = _manager.LastEditValues!;
        Assert.Equal("main", edit.Id);
        Assert.Equal("Primary", edit.Alias);
        // The username was not retyped, so it is not overwritten.
        Assert.Null(edit.Username);
        Assert.Equal("new-pass", edit.Password);
    }

    [Fact]
    public async Task Use_asks_the_manager_to_select_the_account()
    {
        var (viewModel, _) = Create(States.WithAccounts);
        viewModel.SelectedRow = viewModel.Accounts[1];

        await viewModel.UseCommand.ExecuteAsync();

        Assert.Equal("paper", _manager.LastSelectedAccountId);
    }

    [Fact]
    public async Task Delete_needs_a_second_click()
    {
        var (viewModel, _) = Create(States.WithAccounts);
        viewModel.SelectedRow = viewModel.Accounts[1];

        await viewModel.DeleteCommand.ExecuteAsync();
        Assert.Equal(0, _manager.DeleteAccountCalls);
        Assert.Equal("Really delete?", viewModel.DeleteLabel);

        await viewModel.DeleteCommand.ExecuteAsync();
        Assert.Equal(1, _manager.DeleteAccountCalls);
        Assert.Single(viewModel.Accounts);
    }

    private FakeAppManager _manager = new();

    private (AccountsViewModel ViewModel, FakeRuntime Runtime) Create(AppState state)
    {
        _manager = new FakeAppManager { State = state };
        var runtime = new FakeRuntime(_manager);
        return (new AccountsViewModel(_manager, runtime), runtime);
    }

    private static void Seed(FakeRuntime runtime, string id, string alias, string username, string password)
    {
        var settings = runtime.SettingsStore.Load();
        runtime.SettingsStore.Save(settings with
        {
            Accounts = [.. settings.Accounts, new AccountProfile { Id = id, Alias = alias }],
            SelectedAccountId = settings.SelectedAccountId ?? id,
        });

        runtime.Accounts
            .SaveAsync(new AccountEdit(id, alias, new Secret(username), new Secret(password)))
            .GetAwaiter()
            .GetResult();
    }
}
