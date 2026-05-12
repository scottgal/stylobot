using System.Text.Json;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>One session's contribution to centroid computation.</summary>
public sealed record SessionTransitionData(
    RequestState? DominantState,
    IReadOnlyDictionary<(RequestState From, RequestState To), int> Transitions);

/// <summary>
///     Aggregates Markov transition counts across many sessions and walks the resulting
///     matrix greedily to produce an expected request-state chain. Used as the centroid
///     for a cluster of fingerprints.
/// </summary>
public static class SessionChainAggregator
{
    /// <summary>
    ///     Aggregate transition counts from many sessions and greedy-walk a chain.
    /// </summary>
    /// <param name="sessions">Per-session inputs: (DominantState, ParsedTransitionCounts).</param>
    /// <param name="chainLength">Number of states in the resulting chain.</param>
    /// <param name="minTotalTransitions">Floor below which the aggregate is treated as too sparse.</param>
    public static RequestState[] AggregateFromTransitions(
        IReadOnlyList<SessionTransitionData> sessions,
        int chainLength,
        int minTotalTransitions)
    {
        if (sessions.Count == 0 || chainLength <= 0)
            return Array.Empty<RequestState>();

        var aggregate = new Dictionary<(RequestState From, RequestState To), int>();
        var dominantCounts = new Dictionary<RequestState, int>();
        var totalTransitions = 0;

        foreach (var session in sessions)
        {
            foreach (var kv in session.Transitions)
            {
                aggregate[kv.Key] = aggregate.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
                totalTransitions += kv.Value;
            }
            if (session.DominantState.HasValue)
            {
                var d = session.DominantState.Value;
                dominantCounts[d] = dominantCounts.TryGetValue(d, out var c) ? c + 1 : 1;
            }
        }

        if (totalTransitions < minTotalTransitions || dominantCounts.Count == 0)
            return Array.Empty<RequestState>();

        var start = dominantCounts.MaxBy(kv => kv.Value).Key;
        var chain = new List<RequestState>(chainLength) { start };

        var current = start;
        for (var i = 1; i < chainLength; i++)
        {
            (RequestState To, int Count)? best = null;
            foreach (var kv in aggregate)
            {
                if (kv.Key.From != current) continue;
                if (best is null || kv.Value > best.Value.Count)
                    best = (kv.Key.To, kv.Value);
            }
            if (best is null) break;
            chain.Add(best.Value.To);
            current = best.Value.To;
        }

        return chain.ToArray();
    }

    /// <summary>Parse the TransitionCountsJson Dictionary into a typed map.</summary>
    public static Dictionary<(RequestState From, RequestState To), int> ParseTransitionCounts(string json)
    {
        var result = new Dictionary<(RequestState From, RequestState To), int>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var key = prop.Name;
                var arrow = key.IndexOf("->", StringComparison.Ordinal);
                if (arrow <= 0) continue;
                var fromStr = key.Substring(0, arrow);
                var toStr = key.Substring(arrow + 2);
                if (!Enum.TryParse<RequestState>(fromStr, ignoreCase: true, out var from) || !Enum.IsDefined(from)) continue;
                if (!Enum.TryParse<RequestState>(toStr, ignoreCase: true, out var to) || !Enum.IsDefined(to)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                if (!prop.Value.TryGetInt32(out var count)) continue;
                result[(from, to)] = count;
            }
        }
        catch (JsonException)
        {
            // ignore: return empty
        }
        return result;
    }

    /// <summary>Parse the DominantState string into the enum, returning null if invalid.</summary>
    public static RequestState? ParseDominantState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<RequestState>(value, ignoreCase: true, out var state) && Enum.IsDefined(state)
            ? state
            : null;
    }
}
