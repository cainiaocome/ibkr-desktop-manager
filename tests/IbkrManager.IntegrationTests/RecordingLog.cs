namespace IbkrManager.IntegrationTests;

public sealed class RecordingLog : IAppLog
{
    public List<string> Messages { get; } = [];

    public void Info(string message) => Messages.Add("INFO " + message);

    public void Warn(string message) => Messages.Add("WARN " + message);

    public void Error(string message, Exception? exception = null) =>
        Messages.Add("ERROR " + message + (exception is null ? string.Empty : ": " + exception.Message));
}
