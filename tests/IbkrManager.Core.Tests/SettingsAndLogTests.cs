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
                UsernameVariable = "MY_USER",
                PasswordVariable = "MY_PASS",
                LoginDriver = "manual",
                WriteUserEnvironmentVariables = true,
            });

            var loaded = new JsonSettingsStore(Path.Combine(directory, "settings.json")).Load();

            Assert.Equal(@"D:\IBKR\ntws.exe", loaded.ExecutablePathOverride);
            Assert.Equal("MY_USER", loaded.UsernameVariable);
            Assert.Equal("manual", loaded.LoginDriver);
            Assert.True(loaded.WriteUserEnvironmentVariables);
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
            Assert.Equal("IBKR_USERNAME", store.Load().UsernameVariable);
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
    public void AppSettings_normalize_repairs_blank_values()
    {
        var normalized = new AppSettings
        {
            UsernameVariable = "  ",
            PasswordVariable = "",
            LoginDriver = "Manual",
        }.Normalize();

        Assert.Equal("IBKR_USERNAME", normalized.UsernameVariable);
        Assert.Equal("IBKR_PASSWORD", normalized.PasswordVariable);
        Assert.Equal("manual", normalized.LoginDriver);
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
