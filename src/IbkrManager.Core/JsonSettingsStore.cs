using System.Text.Json;

namespace IbkrManager.Core;

/// <summary>Atomic JSON settings file (write temp, then replace) with a safe fallback to defaults.</summary>
public sealed class JsonSettingsStore(string filePath, IAppLog? log = null) : IAppSettingsStore
{
    private readonly IAppLog _log = log ?? NullAppLog.Instance;

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options);
            return (settings ?? AppSettings.Default).Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read settings from {FilePath}: {ex.Message}. Using defaults.");
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), JsonDefaults.Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}
