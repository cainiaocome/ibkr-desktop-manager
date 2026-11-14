using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class AccountServiceTests
{
    private readonly InMemorySettingsStore _settings = new();
    private readonly InMemoryCredentialStore _store = new();
    private readonly SessionCredentialStore _session = new();
    private readonly RecordingLog _log = new();

    private AccountService CreateService() => new(_settings, _store, _session, _log);

    [Fact]
    public async Task Both_halves_come_from_the_same_account()
    {
        var service = CreateService();
        var main = await service.SaveAsync(new AccountEdit(null, "Main", new Secret("main-user"), new Secret("main-pass")));
        var paper = await service.SaveAsync(new AccountEdit(null, "Paper", new Secret("paper-user"), new Secret("paper-pass")));
        service.Select(paper.Id);

        var resolved = service.Resolve();

        Assert.Equal("paper-user", resolved.Username!.Reveal());
        Assert.Equal("paper-pass", resolved.Password!.Reveal());
        Assert.Equal(paper.Id, resolved.AccountId);
        Assert.Equal("Paper", resolved.AccountAlias);
        Assert.NotEqual(main.Id, resolved.AccountId);

        service.Select(main.Id);

        var other = service.Resolve();

        Assert.Equal("main-user", other.Username!.Reveal());
        Assert.Equal("main-pass", other.Password!.Reveal());
    }

    [Fact]
    public async Task A_half_never_leaks_from_another_account()
    {
        // The old provider chain could pair account A's username with account B's password.
        var service = CreateService();
        await service.SaveAsync(new AccountEdit(null, "Complete", new Secret("first-user"), new Secret("first-pass")));
        var half = await service.SaveAsync(new AccountEdit(null, "Half", new Secret("second-user"), null));
        service.Select(half.Id);

        var resolved = service.Resolve();

        Assert.Equal("second-user", resolved.Username!.Reveal());
        Assert.False(resolved.HasPassword);
        Assert.False(resolved.IsComplete);
        Assert.Equal("the password of this account is not set", resolved.PasswordHint);
    }

    [Fact]
    public async Task Selection_defaults_to_the_first_account_and_is_persisted()
    {
        var service = CreateService();
        var first = await service.SaveAsync(new AccountEdit(null, "Main", new Secret("u1"), new Secret("p1")));
        var second = await service.SaveAsync(new AccountEdit(null, "Paper", new Secret("u2"), new Secret("p2")));

        Assert.Equal(first.Id, service.SelectedId);

        service.Select(second.Id);

        Assert.Equal(second.Id, service.SelectedId);
        Assert.Equal(second.Id, _settings.Load().SelectedAccountId);
        // A new service over the same files starts on the same account: the default at startup.
        Assert.Equal(second.Id, CreateService().SelectedId);
    }

    [Fact]
    public async Task A_dangling_selection_falls_back_to_the_first_account()
    {
        var service = CreateService();
        var account = await service.SaveAsync(new AccountEdit(null, "Main", new Secret("u"), new Secret("p")));
        _settings.Save(_settings.Load() with { SelectedAccountId = "gone" });

        Assert.Equal(account.Id, service.SelectedId);
    }

    [Fact]
    public async Task Typed_this_session_values_win_for_that_account_only()
    {
        var service = CreateService();
        var stored = await service.SaveAsync(new AccountEdit(null, "Main", new Secret("stored-user"), new Secret("stored-pass")));

        await service.SaveAsync(
            new AccountEdit(stored.Id, "Main", new Secret("typed-user"), new Secret("typed-pass"), Persist: false));

        var resolved = service.Resolve();

        Assert.Equal("typed-user", resolved.Username!.Reveal());
        Assert.Equal(CredentialSource.Session, resolved.UsernameSource);
        Assert.Equal("stored-pass", _store.Read(stored.Id, CredentialKind.Password));
    }

    [Fact]
    public async Task Editing_only_the_alias_keeps_the_stored_pair()
    {
        var service = CreateService();
        var account = await service.SaveAsync(new AccountEdit(null, "Main", new Secret("u"), new Secret("p")));

        await service.SaveAsync(new AccountEdit(account.Id, "Renamed", null, null));

        var resolved = service.Resolve();
        Assert.Equal("Renamed", resolved.AccountAlias);
        Assert.Equal("u", resolved.Username!.Reveal());
        Assert.Equal("p", resolved.Password!.Reveal());
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_secrets_and_selects_another()
    {
        var service = CreateService();
        var first = await service.SaveAsync(new AccountEdit(null, "Main", new Secret("u1"), new Secret("p1")));
        var second = await service.SaveAsync(new AccountEdit(null, "Paper", new Secret("u2"), new Secret("p2")));
        service.Select(second.Id);

        await service.DeleteAsync(second.Id);

        Assert.Null(_store.Read(second.Id, CredentialKind.Password));
        Assert.Single(service.List());
        Assert.Equal(first.Id, service.SelectedId);
    }

    [Fact]
    public async Task Duplicate_aliases_and_blank_names_are_rejected()
    {
        var service = CreateService();
        await service.SaveAsync(new AccountEdit(null, "Main", new Secret("u"), new Secret("p")));

        await Assert.ThrowsAsync<AppManagerException>(
            () => service.SaveAsync(new AccountEdit(null, "main", new Secret("u2"), new Secret("p2"))));
        await Assert.ThrowsAsync<AppManagerException>(
            () => service.SaveAsync(new AccountEdit(null, "   ", new Secret("u3"), new Secret("p3"))));
    }

    [Fact]
    public void The_legacy_single_credential_is_migrated_once()
    {
        _store.LegacyUsername = "legacy-user";
        _store.LegacyPassword = "legacy-pass";

        var service = CreateService();

        var account = Assert.Single(service.List());
        Assert.Equal("Default", account.Alias);
        Assert.True(account.IsComplete);
        Assert.Equal(account.Id, service.SelectedId);
        Assert.Equal("legacy-user", service.Resolve().Username!.Reveal());
        Assert.Equal("legacy-user", _store.Read(account.Id, CredentialKind.Username));

        // The old target is left alone so a downgrade still finds it.
        Assert.Equal("legacy-pass", _store.ReadLegacy(CredentialKind.Password));

        // Running again must not create a second account.
        Assert.Single(CreateService().List());
    }

    [Fact]
    public async Task Migration_does_not_touch_an_existing_account_list()
    {
        var service = CreateService();
        await service.SaveAsync(new AccountEdit(null, "Main", new Secret("u"), new Secret("p")));

        // A legacy pair appearing later (a 0.3.x credential written while this build was running).
        _store.LegacyUsername = "legacy-user";

        var list = CreateService().List();

        Assert.Single(list);
        Assert.Equal("Main", list[0].Alias);
    }

    [Fact]
    public void No_accounts_means_an_explicit_empty_resolution()
    {
        var service = CreateService();

        var resolved = service.Resolve();

        Assert.Null(resolved.AccountId);
        Assert.False(resolved.IsComplete);
        Assert.Equal("no account is selected", resolved.UsernameHint);
    }
}
