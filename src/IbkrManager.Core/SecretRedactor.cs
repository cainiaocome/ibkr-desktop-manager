namespace IbkrManager.Core;

/// <summary>
/// Last line of defence: scrubs known secret values out of any text before it reaches a sink.
/// Values shorter than four characters are ignored so that a pathological one-character
/// password cannot turn every log line into asterisks.
/// </summary>
public sealed class SecretRedactor
{
    public const string Replacement = "***";
    private const int MinimumLength = 4;

    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumLength)
        {
            return;
        }

        lock (_gate)
        {
            _secrets.Add(value);
        }
    }

    public void Register(Secret? secret)
    {
        if (secret is not null && !secret.IsEmpty)
        {
            Register(secret.Reveal());
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string[] secrets;
        lock (_gate)
        {
            if (_secrets.Count == 0)
            {
                return text;
            }

            secrets = [.. _secrets.OrderByDescending(s => s.Length)];
        }

        foreach (var secret in secrets)
        {
            text = text.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        return text;
    }
}
