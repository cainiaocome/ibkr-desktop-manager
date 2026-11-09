using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

/// <summary>
/// These exercise the real Windows APIs and are no-ops elsewhere; the pure logic above runs
/// everywhere so Linux/macOS CI can still build and test the platform project.
/// </summary>
public sealed class WindowsEnvironmentAndCredentialTests
{
    [Fact]
    public void User_environment_round_trips_a_scoped_test_variable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var environment = new WindowsUserEnvironment();
        var name = "IBKR_MANAGER_TEST_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            environment.Write(name, "first");
            Assert.Equal("first", environment.Read(name));

            environment.Write(name, "second");
            Assert.Equal("second", environment.Read(name));
        }
        finally
        {
            environment.Delete(name);
            Assert.Null(environment.Read(name));
        }
    }

    [Fact]
    public async Task Credential_store_round_trips_and_clears()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var log = NullAppLog.Instance;
        var store = new WindowsCredentialStore(new WindowsUserEnvironment(), () => AppSettings.Default, log);

        try
        {
            using var username = new Secret("test-user-" + Guid.NewGuid().ToString("N")[..6]);
            using var password = new Secret("test-password-value");

            await store.SaveAsync(username, password, CredentialWriteOptions.Default);

            Assert.Equal(username.Reveal(), store.Read(CredentialKind.Username));
            Assert.Equal(password.Reveal(), store.Read(CredentialKind.Password));
            Assert.True(store.HasStoredValue(CredentialKind.Password));
        }
        finally
        {
            await store.ClearAsync();
        }

        Assert.Null(store.Read(CredentialKind.Username));
        Assert.False(store.HasStoredValue(CredentialKind.Password));
    }

    [Fact]
    public void Job_object_can_be_created_and_disposed_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var job = JobObject.CreateKillOnClose();
        Assert.NotNull(job);
    }
}
