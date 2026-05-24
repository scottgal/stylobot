namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Per-signature nearest-bot-neighbour lookup, feeds the behavioural
///     grouper's tier 3 (session-vector cosine). Cheap on the hot path:
///     hits return cached results; misses kick off a background populate
///     and return null so the grouper falls through to a lower tier.
/// </summary>
public interface ISessionSimilarityLookup
{
    /// <summary>
    ///     Returns the most similar bot-shaped signature for <paramref name="signature"/>
    ///     when the cached cosine is at or above <paramref name="minCosine"/>.
    ///     Returns null when no neighbour meets the threshold OR when this
    ///     signature hasn't been looked up yet (cold cache).
    /// </summary>
    SimilarityNeighbour? FindNearestBotNeighbour(string signature, float minCosine);
}

public sealed record SimilarityNeighbour(string Signature, float Cosine);
