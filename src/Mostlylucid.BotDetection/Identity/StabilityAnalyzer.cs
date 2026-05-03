namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Retroactive stability analysis across sessions for an entity.
///     For each header hash, computes:
///     - PersonalStability: how consistent is this hash across sessions for THIS entity?
///     - GlobalRarity: how rare is this hash value across ALL entities?
///     - AnchorStrength: PersonalStability × GlobalRarity (high = strong identity anchor)
///
///     A header that's stable for one entity but rare globally is a powerful anchor.
///     A header that's stable for everyone (Accept-Encoding: gzip, br) is useless.
/// </summary>
public static class StabilityAnalyzer
{
    /// <summary>
    ///     Compute anchor strengths for an entity's header hashes across sessions.
    /// </summary>
    /// <param name="entitySessionHashes">
    ///     Header hashes from each session belonging to this entity.
    ///     Outer list = sessions (ordered by time), inner dict = header name → hash value.
    /// </param>
    /// <param name="globalHashCounts">
    ///     For each header name → hash value, how many distinct entities have this hash.
    ///     Used to compute GlobalRarity. Null = skip rarity (use stability only).
    /// </param>
    /// <param name="totalEntityCount">Total number of entities for rarity calculation.</param>
    /// <returns>Dictionary of header name → AnchorScore.</returns>
    public static Dictionary<string, AnchorScore> ComputeAnchors(
        IReadOnlyList<Dictionary<string, string>> entitySessionHashes,
        Dictionary<string, Dictionary<string, int>>? globalHashCounts = null,
        int totalEntityCount = 1)
    {
        if (entitySessionHashes.Count == 0) return new();

        var anchors = new Dictionary<string, AnchorScore>(StringComparer.OrdinalIgnoreCase);

        // Collect all header names seen across sessions
        var allHeaders = entitySessionHashes
            .SelectMany(h => h.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var headerName in allHeaders)
        {
            // Collect all hash values for this header across sessions
            var values = entitySessionHashes
                .Where(h => h.ContainsKey(headerName))
                .Select(h => h[headerName])
                .ToList();

            if (values.Count == 0) continue;

            // Personal stability: how consistent is this value?
            var grouped = values.GroupBy(v => v).OrderByDescending(g => g.Count()).ToList();
            var mostCommonCount = grouped[0].Count();
            var totalSessions = entitySessionHashes.Count;
            var personalStability = (double)mostCommonCount / totalSessions;

            // Presence ratio: in how many sessions does this header appear?
            var presenceRatio = (double)values.Count / totalSessions;

            // Global rarity: how rare is this hash value across all entities?
            var globalRarity = 1.0;
            if (globalHashCounts != null && totalEntityCount > 1 &&
                globalHashCounts.TryGetValue(headerName, out var hashCounts))
            {
                var mostCommonHash = grouped[0].Key;
                var entitiesWithThisHash = hashCounts.GetValueOrDefault(mostCommonHash, 1);
                // Rarity = 1 - (fraction of entities with this hash)
                // Rare hash → high rarity → strong anchor
                globalRarity = 1.0 - ((double)entitiesWithThisHash / totalEntityCount);
                globalRarity = Math.Max(0.01, globalRarity); // Floor to avoid zero
            }

            var anchorStrength = personalStability * globalRarity;

            anchors[headerName] = new AnchorScore
            {
                HeaderName = headerName,
                MostCommonHash = grouped[0].Key,
                PersonalStability = personalStability,
                PresenceRatio = presenceRatio,
                GlobalRarity = globalRarity,
                AnchorStrength = anchorStrength,
                SessionCount = totalSessions,
                UniqueValues = grouped.Count
            };
        }

        return anchors;
    }
}

/// <summary>
///     Identity anchor score for a single header/factor.
/// </summary>
public sealed record AnchorScore
{
    public required string HeaderName { get; init; }
    public required string MostCommonHash { get; init; }

    /// <summary>How consistent is this hash for THIS entity? (0-1, higher = more stable)</summary>
    public double PersonalStability { get; init; }

    /// <summary>In what fraction of sessions does this header appear? (0-1)</summary>
    public double PresenceRatio { get; init; }

    /// <summary>How rare is this hash value globally? (0-1, higher = rarer)</summary>
    public double GlobalRarity { get; init; }

    /// <summary>PersonalStability × GlobalRarity. High = strong identity anchor.</summary>
    public double AnchorStrength { get; init; }

    /// <summary>Number of sessions analyzed.</summary>
    public int SessionCount { get; init; }

    /// <summary>Number of distinct hash values seen (1 = perfectly stable).</summary>
    public int UniqueValues { get; init; }
}
