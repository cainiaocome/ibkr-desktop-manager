using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class SettingsAndLogTests
{
    [Fact]
    public void JsonSettingsStore_round_trips()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings
            {
                ExecutablePathOverride = @"D:\IBKR\ntws.exe",
                LoginDriver = "manual",
                Accounts = [new AccountProfile { Id = "account-1", Alias = "Main" }],
                SelectedAccountId = "account-1",
            });

            var loaded = new JsonSettingsStore(Path.Combine(directory, "settings.json")).Load();

            Assert.Equal(@"D:\IBKR\ntws.exe", loaded.ExecutablePathOverride);
            Assert.Equal("manual", loaded.LoginDriver);
            Assert.Equal("account-1", loaded.SelectedAccountId);
            var account = Assert.Single(loaded.Accounts);
            Assert.Equal("Main", account.Alias);
            // Aliases are not secrets, but usernames and passwords must never reach the file.
            var json = File.ReadAllText(Path.Combine(directory, "settings.json"));
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonSettingsStore_falls_back_to_defaults_for_a_missing_file()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "missing.json"));
            Assert.Empty(store.Load().Accounts);
            Assert.Null(store.Load().SelectedAccountId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_03x_settings_file_still_loads()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            // The 0.3.x shape: environment-variable names and the "also write env" flag are gone.
            File.WriteAllText(path, """
                {
                  "executablePathOverride": "D:\\IBKR\\ntws.exe",
                  "usernameVariable": "MY_USER",
                  "passwordVariable": "MY_PASS",
                  "writeUserEnvironmentVariables": true,
                  "loginDriver": "manual"
                }
                """);

            var loaded = new JsonSettingsStore(path).Load();

            Assert.Equal(@"D:\IBKR\ntws.exe", loaded.ExecutablePathOverride);
            Assert.Equal("manual", loaded.LoginDriver);
            Assert.Empty(loaded.Accounts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonSettingsStore_falls_back_to_defaults_for_corrupt_json()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ this is not json");
            var log = new RecordingLog();

            var loaded = new JsonSettingsStore(path, log).Load();

            Assert.Equal(AppSettings.Default, loaded);
            Assert.Single(log.Warnings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AppSettings_normalize_repairs_accounts_and_the_selection()
    {
        var normalized = new AppSettings
        {
            LoginDriver = "Manual",
            Accounts =
            [
                new AccountProfile { Id = "a", Alias = "  " },
                new AccountProfile { Id = "a", Alias = "duplicate id" },
                new AccountProfile { Id = "", Alias = "no id" },
                new AccountProfile { Id = "b", Alias = " Paper " },
            ],
            SelectedAccountId = "missing",
        }.Normalize();

        Assert.Equal("manual", normalized.LoginDriver);
        Assert.Equal(2, normalized.Accounts.Count);
        Assert.Equal("Account 1", normalized.Accounts[0].Alias);
        Assert.Equal("Paper", normalized.Accounts[1].Alias);
        Assert.Equal("a", normalized.SelectedAccountId);
    }

    [Fact]
    public void AppSettings_normalize_rejects_unknown_drivers()
    {
        var normalized = new AppSettings { LoginDriver = "robots" }.Normalize();
        Assert.Equal("auto", normalized.LoginDriver);
    }

    [Fact]
    public void FileLog_writes_levelled_lines()
    {
        var directory = CreateTempDirectory();
        try
        {
            using (var log = new FileLog(directory))
            {
                log.Info("started");
                log.Warn("careful");
                log.Error("broke", new InvalidOperationException("nope"));
            }

            var content = File.ReadAllText(Directory.GetFiles(directory, "manager-*.log").Single());

            Assert.Contains("[INFO] started", content);
            Assert.Contains("[WARN] careful", content);
            Assert.Contains("[ERROR] broke: InvalidOperationException: nope", content);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileLog_never_throws_when_the_directory_is_unusable()
    {
        using var log = new FileLog(Path.Combine(Path.GetTempPath(), "ibkr-manager-missing", Guid.NewGuid().ToString("N")));
        log.Info("still fine");
    }

    [Fact]
    public void InMemorySettingsStore_normalizes_on_save()
    {
        var store = new InMemorySettingsStore();
        store.Save(new AppSettings { LoginDriver = "MANUAL" });

        Assert.Equal("manual", store.Load().LoginDriver);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ibkr-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
