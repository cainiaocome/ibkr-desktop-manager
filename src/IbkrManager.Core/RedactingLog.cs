namespace IbkrManager.Core;

/// <summary>Wraps another log and scrubs registered secrets from every message.</summary>
public sealed class RedactingLog(IAppLog inner, SecretRedactor redactor) : IAppLog
{
    public void Info(string message) => inner.Info(redactor.Redact(message));

    public void Warn(string message) => inner.Warn(redactor.Redact(message));

    public void Error(string message, Exception? exception = null) =>
        inner.Error(redactor.Redact(message), exception is null ? null : new RedactedException(exception));
}

/// <summary>
/// Marker exception that keeps the original message out of sink formatting.
/// </summary>
public sealed class RedactedException(Exception inner) : Exception(inner.GetType().Name, inner);
