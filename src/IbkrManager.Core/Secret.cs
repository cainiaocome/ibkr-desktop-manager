using System.Diagnostics;

namespace IbkrManager.Core;

/// <summary>
/// Wraps a credential so that it cannot be logged or interpolated by accident.
/// <see cref="ToString"/> is deliberately useless; call <see cref="Reveal"/> at the point of use.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class Secret : IDisposable
{
    private string? _value;

    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public int Length => _value?.Length ?? 0;

    public static Secret? From(string? value) => string.IsNullOrEmpty(value) ? null : new Secret(value);

    public string Reveal()
    {
        ObjectDisposedException.ThrowIf(_value is null, this);
        return _value;
    }

    /// <summary>Equality without exposing the value to a comparer dump.</summary>
    public bool Matches(Secret? other) =>
        other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public void Dispose() => _value = null;

    private string DebuggerDisplay => "***";

    public override string ToString() => "***";
}
