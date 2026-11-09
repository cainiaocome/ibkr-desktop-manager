namespace IbkrManager.Core;

/// <summary>Minimal logging seam. Implementations must never be handed raw credentials.</summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    private NullAppLog()
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
