using Xunit;

namespace IbkrManager.Core.Tests;

/// <summary>
/// settings.json now holds the account list, so a bad read must not turn into an empty write.
/// </summary>
public sealed class SettingsFileSafetyTests
{
    [Fact]
    public void An_unreadable_file_is_kept_before_it_is_overwritten()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ broken json");
            var log = new RecordingLog();
            var store = new JsonSettingsStore(path, log);

            var loaded = store.Load();
            Assert.Empty(loaded.Accounts);

            store.Save(new AppSettings { Accounts = [new AccountProfile { Id = "a", Alias = "Main" }] });

            var backup = Assert.Single(Directory.GetFiles(directory, "settings.json.unreadable-*"));
            Assert.Equal("{ broken json", File.ReadAllText(backup));
            Assert.Single(new JsonSettingsStore(path).Load().Accounts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_failed_read_does_not_lose_accounts_on_the_next_save()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var original = new AppSettings
            {
                Accounts = [new AccountProfile { Id = "a", Alias = "Main" }],
                SelectedAccountId = "a",
            };
            new JsonSettingsStore(path).Save(original);

            // Simulate an unreadable file by locking it exclusively.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failing = new JsonSettingsStore(path);
                Assert.Empty(failing.Load().Accounts);
            }

            // Once readable again, the accounts are still there.
            Assert.Single(new JsonSettingsStore(path).Load().Accounts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Concurrent_saves_do_not_collide_on_a_temp_file()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);

            Parallel.For(0, 20, index => store.Save(new AppSettings { LoginDriver = index % 2 == 0 ? "auto" : "manual" }));

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ibkr-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
