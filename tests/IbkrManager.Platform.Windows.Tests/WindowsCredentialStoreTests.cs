using Xunit;

namespace IbkrManager.Platform.Windows.Tests;

/// <summary>
/// Exercises the real Credential Manager and is a no-op elsewhere; the rest of the platform project
/// runs everywhere so Linux/macOS CI can still build and test it.
/// </summary>
public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public async Task Each_account_round_trips_its_own_pair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialStore(NullAppLog.Instance);
        var first = "test-" + Guid.NewGuid().ToString("N")[..10];
        var second = "test-" + Guid.NewGuid().ToString("N")[..10];

        try
        {
            using var firstUser = new Secret("user-one");
            using var firstPassword = new Secret("password-one");
            using var secondUser = new Secret("user-two");
            using var secondPassword = new Secret("password-two");

            await store.SaveAsync(first, firstUser, firstPassword);
            await store.SaveAsync(second, secondUser, secondPassword);

            Assert.Equal("user-one", store.Read(first, CredentialKind.Username));
            Assert.Equal("password-one", store.Read(first, CredentialKind.Password));
            Assert.Equal("user-two", store.Read(second, CredentialKind.Username));
            Assert.Equal("password-two", store.Read(second, CredentialKind.Password));

            // Clearing one account must not touch the other.
            await store.ClearAsync(first);
            Assert.Null(store.Read(first, CredentialKind.Username));
            Assert.Equal("user-two", store.Read(second, CredentialKind.Username));

            // The legacy single-credential target is not used for accounts.
            Assert.Null(store.ReadLegacy(CredentialKind.Username));
        }
        finally
        {
            await store.ClearAsync(first);
            await store.ClearAsync(second);
        }
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
