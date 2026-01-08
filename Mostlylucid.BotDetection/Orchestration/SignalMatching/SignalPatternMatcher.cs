using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.SignalMatching;

/// <summary>
///     Signal pattern matcher for dynamic signal resolution.
///     Uses ephemeral's SignalKey pattern matching to extract signals by pattern.
/// </summary>
public sealed class SignalPatternMatcher
{
    private readonly Dictionary<string, string> _patterns;

    public SignalPatternMatcher(Dictionary<string, string> patterns)
    {
        _patterns = patterns;
    }

    /// <summary>
    ///     Extract signals matching configured patterns from sink.
    ///     Returns a dictionary with semantic names (from pattern keys) mapped to signal values.
    /// </summary>
    /// <example>
    ///     Pattern: ["risk"] = "request.*.risk"
    ///     Matches: request.detector.risk, request.heuristic.risk
    ///     Returns: ["risk"] = "0.85" (as string from Key property)
    /// </example>
    public Dictionary<string, object> ExtractFrom(SignalSink sink)
    {
        var extracted = new Dictionary<string, object>();

        foreach (var (name, pattern) in _patterns)
        {
            // Ephemeral 1.6.8: Convert pattern to predicate
            // Pattern "request.*.risk" means: starts with "request." and ends with ".risk"
            var matches = sink.Sense(evt => MatchesPattern(evt.Signal, pattern));

            if (matches.Any())
            {
                // Get latest match for this pattern
                var latest = matches.OrderByDescending(e => e.Timestamp).First();
                // In ephemeral 1.6.8, the value is in the Key property
                extracted[name] = latest.Key ?? latest.Signal;
            }
        }

        return extracted;
    }

    /// <summary>
    ///     Extract all signals matching a single pattern.
    ///     Returns the latest value for the pattern.
    /// </summary>
    public object? ExtractSingle(SignalSink sink, string pattern)
    {
        var matches = sink.Sense(evt => MatchesPattern(evt.Signal, pattern));
        var latest = matches.OrderByDescending(e => e.Timestamp).FirstOrDefault();
        // Return the value from Key property
        return latest == default ? null : latest.Key ?? latest.Signal;
    }

    /// <summary>
    ///     Simple pattern matching: supports "*" wildcard.
    ///     "request.*.risk" matches "request.detector.risk", "request.heuristic.risk"
    /// </summary>
    private static bool MatchesPattern(string signal, string pattern)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains("*")) return signal == pattern;

        // Simple wildcard matching
        var parts = pattern.Split('*');
        if (parts.Length != 2) return false; // Only support one wildcard for now

        var prefix = parts[0];
        var suffix = parts[1];

        return signal.StartsWith(prefix) && signal.EndsWith(suffix);
    }

    /// <summary>
    ///     Extract typed signal value.
    /// </summary>
    public T? ExtractTyped<T>(SignalSink sink, string pattern)
    {
        var value = ExtractSingle(sink, pattern);
        return value is T typed ? typed : default;
    }
}