namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Kind of value a signal carries. Inferred from the leading word of the
///     XML doc summary (<c>String:</c>, <c>Bool:</c>, <c>Float:</c>, etc.)
///     and overridable via the signal overlay JSON.
/// </summary>
public enum SignalKind
{
    /// <summary>Single string scalar.</summary>
    String,
    /// <summary>Boolean true/false.</summary>
    Bool,
    /// <summary>Numeric (decimal/double/int).</summary>
    Number,
    /// <summary>Single value drawn from a known closed set; the overlay supplies the value domain.</summary>
    EnumOf,
    /// <summary>Array of values; set operators (any in, all in, contains) apply.</summary>
    Array
}
