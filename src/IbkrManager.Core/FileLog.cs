using System.Text;

namespace IbkrManager.Core;

/// <summary>
/// Small rolling file logger. One file per day, oldest pruned, shared-write so two instances
/// (or a test) cannot lock each other out. Secrets must already be redacted by the caller.
/// </summary>
public sealed class FileLog : IAppLog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly int _retainDays;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public FileLog(string directory, int retainDays = 7)
    {
        _directory = directory;
        _retainDays = Math.Max(1, retainDays);
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    public string CurrentFilePath
    {
        get
        {
            lock (_gate)
            {
                return Path.Combine(_directory, $"manager-{Today():yyyy-MM-dd}.log");
            }
        }
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    private void Write(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = new StringBuilder(message.Length + 40)
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ")
            .AppendLine(message.ReplaceLineEndings(" "))
            .ToString();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter();
                _writer!.Write(line);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
                System.Diagnostics.Debug.WriteLine($"file log write failed: {ex.Message}");
            }
        }
    }

    private void EnsureWriter()
    {
        var today = Today();
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        _writer?.Dispose();
        _writerDate = today;
        var path = Path.Combine(_directory, $"manager-{today:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, Encoding.UTF8);
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = Today().AddDays(-_retainDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "manager-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file)["manager-".Length..];
                if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var date) && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"log prune failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
