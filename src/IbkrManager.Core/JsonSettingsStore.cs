using System.Text.Json;

namespace IbkrManager.Core;

/// <summary>Atomic JSON settings file (write temp, then replace) with a safe fallback to defaults.</summary>
public sealed class JsonSettingsStore(string filePath, IAppLog? log = null) : IAppSettingsStore
{
    private readonly IAppLog _log = log ?? NullAppLog.Instance;
    private readonly object _gate = new();

    /// <summary>
    /// True when the last load fell back to defaults. Settings now hold the account list, so writing
    /// over an unreadable file would silently drop every account; the original is kept instead.
    /// </summary>
    private bool _lastLoadFailed;

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
            _lastLoadFailed = false;
            return (settings ?? AppSettings.Default).Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read settings from {FilePath}: {ex.Message}. Using defaults.");
            _lastLoadFailed = true;
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

        lock (_gate)
        {
            if (_lastLoadFailed && File.Exists(FilePath))
            {
                // Keep what we could not read instead of overwriting it with defaults.
                var backup = FilePath + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
                try
                {
                    File.Copy(FilePath, backup, overwrite: true);
                    _log.Warn($"Kept a copy of the unreadable settings at {backup}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"Could not back up the unreadable settings: {ex.Message}");
                }
            }

            // Unique temp name: a tick on the thread pool and a dialog on the UI thread can both save.
            var temp = $"{FilePath}.tmp-{Guid.NewGuid():N}";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), JsonDefaults.Options));
                File.Move(temp, FilePath, overwrite: true);
                _lastLoadFailed = false;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
    }
}
