using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class CredentialResolverTests
{
    [Fact]
    public void First_provider_wins_and_source_is_reported()
    {
        var resolver = new CredentialResolver(
        [
            new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "env-user", "env-pass"),
            new FakeCredentialProvider(CredentialSource.CredentialManager, "stored-user", "stored-pass"),
        ]);

        var resolved = resolver.Resolve();

        Assert.Equal("env-user", resolved.Username!.Reveal());
        Assert.Equal("env-pass", resolved.Password!.Reveal());
        Assert.Equal(CredentialSource.ProcessEnvironment, resolved.UsernameSource);
        Assert.Equal(CredentialSource.ProcessEnvironment, resolved.PasswordSource);
        Assert.True(resolved.IsComplete);
    }

    [Fact]
    public void Providers_can_be_mixed_per_field()
    {
        var resolver = new CredentialResolver(
        [
            new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "env-user", null),
            new FakeCredentialProvider(CredentialSource.CredentialManager, null, "stored-pass"),
        ]);

        var resolved = resolver.Resolve();

        Assert.Equal(CredentialSource.ProcessEnvironment, resolved.UsernameSource);
        Assert.Equal(CredentialSource.CredentialManager, resolved.PasswordSource);
        Assert.True(resolved.IsComplete);
        Assert.Contains("env", resolved.SourcesDescription);
    }

    [Fact]
    public void Unavailable_providers_are_skipped()
    {
        var unavailable = new FakeCredentialProvider(CredentialSource.CredentialManager, "stored-user", "stored-pass") { IsAvailable = false };
        var resolver = new CredentialResolver(
        [
            unavailable,
            new FakeCredentialProvider(CredentialSource.Session, "session-user", "session-pass"),
        ]);

        var resolved = resolver.Resolve();

        Assert.Equal("session-user", resolved.Username!.Reveal());
        Assert.Equal(CredentialSource.Session, resolved.UsernameSource);
    }

    [Fact]
    public void Missing_values_produce_actionable_hints()
    {
        var resolver = new CredentialResolver(
        [
            new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "env-user", null, "IBKR_PASSWORD"),
        ]);

        var resolved = resolver.Resolve();

        Assert.False(resolved.IsComplete);
        Assert.Contains("password", resolved.MissingDescription);
        Assert.Contains("IBKR_PASSWORD", resolved.PasswordHint);
    }

    [Fact]
    public void Empty_store_returns_no_credentials()
    {
        var resolver = new CredentialResolver([new FakeCredentialProvider(CredentialSource.ProcessEnvironment, null, null)]);

        var resolved = resolver.Resolve();

        Assert.Null(resolved.Username);
        Assert.Null(resolved.Password);
        Assert.False(resolved.IsComplete);
        Assert.Contains("username and password", resolved.MissingDescription);
    }

    [Fact]
    public void Resolved_values_are_registered_with_the_redactor()
    {
        var redactor = new SecretRedactor();
        var resolver = new CredentialResolver(
            [new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "user-name", "password-value")],
            redactor);

        _ = resolver.Resolve();

        Assert.Equal("***", redactor.Redact("password-value"));
    }

    [Fact]
    public void ResolvedCredentials_ToString_never_leaks()
    {
        var resolver = new CredentialResolver([new FakeCredentialProvider(CredentialSource.ProcessEnvironment, "alice-zz", "s3cr3t-value")]);
        var resolved = resolver.Resolve();

        Assert.DoesNotContain("alice-zz", resolved.ToString());
        Assert.DoesNotContain("s3cr3t-value", resolved.ToString());
    }
}
