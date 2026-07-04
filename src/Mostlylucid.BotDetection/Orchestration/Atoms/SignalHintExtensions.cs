using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Extension methods for reading Model-2 hint values off a
///     <see cref="SignalSink"/>. Model 2 signals encode a value after a colon
///     (e.g. <c>"signature.primary:abc123"</c>); this helper parses the value
///     back out for atoms that need boundary hint reads.
/// </summary>
/// <remarks>
///     Consumers should still treat hints as OPTIMISATIONS and query the
///     source-of-truth atom for authoritative state per
///     <c>SIGNALS_PATTERN.md</c>. Hints can be stale; the atom is truth.
/// </remarks>
public static class SignalHintExtensions
{
    /// <summary>
    ///     Reads the latest Model-2 hint value for <paramref name="prefix"/>.
    ///     Returns null if no signal with that prefix was raised in the
    ///     current window.
    /// </summary>
    public static string? ReadHint(this SignalSink sink, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;

        var needle = prefix + ":";
        var signals = sink.Sense(s => s.Signal.StartsWith(needle, StringComparison.Ordinal));
        if (signals.Count == 0) return null;

        // Latest wins -- Model 2 hints may be stale between emissions; freshest
        // is closest to source of truth.
        var latest = signals[0];
        for (var i = 1; i < signals.Count; i++)
        {
            if (signals[i].Timestamp > latest.Timestamp) latest = signals[i];
        }

        return latest.Signal[needle.Length..];
    }

    /// <summary>
    ///     Reads all Model-2 hint values raised under <paramref name="prefix"/>
    ///     during the current window, ordered by timestamp ascending. Useful
    ///     for atoms that need every emission (e.g. counting distinct values,
    ///     replaying an event stream).
    /// </summary>
    public static IReadOnlyList<string> ReadHints(this SignalSink sink, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return Array.Empty<string>();

        var needle = prefix + ":";
        var signals = sink.Sense(s => s.Signal.StartsWith(needle, StringComparison.Ordinal));
        if (signals.Count == 0) return Array.Empty<string>();

        var sorted = new List<(DateTimeOffset Timestamp, string Value)>(signals.Count);
        foreach (var s in signals)
        {
            sorted.Add((s.Timestamp, s.Signal[needle.Length..]));
        }
        sorted.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        var values = new string[sorted.Count];
        for (var i = 0; i < sorted.Count; i++) values[i] = sorted[i].Value;
        return values;
    }
}
