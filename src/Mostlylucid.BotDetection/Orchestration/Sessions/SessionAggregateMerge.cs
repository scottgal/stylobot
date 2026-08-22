namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Merge policy for combining a <see cref="SessionSample"/> into an
///     existing <see cref="SessionAggregate"/>. Kept as a static helper so
///     escalators, the session atom, and tests share one canonical
///     definition of "how do samples aggregate."
/// </summary>
public static class SessionAggregateMerge
{
    /// <summary>Folds cumulative, source-owned aggregates without double-counting them.</summary>
    public static SessionAggregate Fold(IEnumerable<SessionAggregate> aggregates)
    {
        var all = aggregates.ToArray();
        if (all.Length == 0) throw new ArgumentException("At least one aggregate is required.", nameof(aggregates));
        var count = all.Sum(a => a.SampleCount);
        var mean = count == 0 ? 0 : all.Sum(a => a.MeanBotProbability * a.SampleCount) / count;
        var statuses = new Dictionary<int, int>();
        foreach (var aggregate in all)
            foreach (var pair in aggregate.UpstreamStatusCounts)
                statuses[pair.Key] = statuses.GetValueOrDefault(pair.Key) + pair.Value;
        var latest = all.OrderByDescending(a => a.LastSample).First();
        var honeypots = all.Sum(a => a.HoneypotHits);
        return latest with
        {
            FirstSample = all.Min(a => a.FirstSample),
            LastSample = all.Max(a => a.LastSample),
            SampleCount = count,
            MeanBotProbability = mean,
            MaxBotProbability = all.Max(a => a.MaxBotProbability),
            HoneypotHits = honeypots,
            UpstreamStatusCounts = statuses,
            RetentionPriority = ComputeRetentionPriority(mean, latest.LatestConfidence, honeypots),
        };
    }
    /// <summary>
    ///     Build the initial aggregate from the first sample seen for a
    ///     fingerprint. Called on
    ///     <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, TValue, System.Func{TKey,TValue,TValue})"/>
    ///     miss path in <see cref="SessionStore"/>.
    /// </summary>
    public static SessionAggregate FromFirstSample(SessionSample sample)
    {
        var statusCounts = new Dictionary<int, int>();
        if (sample.FromUpstream) statusCounts[sample.StatusCode] = 1;

        return new SessionAggregate
        {
            FingerprintId = sample.FingerprintId,
            SiteId = sample.SiteId,
            FirstSample = sample.Timestamp,
            LastSample = sample.Timestamp,
            SampleCount = 1,
            MeanBotProbability = sample.BotProbability,
            MaxBotProbability = sample.BotProbability,
            LatestConfidence = sample.Confidence,
            HoneypotHits = sample.Honeypot ? 1 : 0,
            UpstreamStatusCounts = statusCounts,
            DominantClientType = sample.ClientType,
            RetentionPriority = ComputeRetentionPriority(
                sample.BotProbability,
                sample.Confidence,
                honeypotHits: sample.Honeypot ? 1 : 0),
        };
    }

    /// <summary>
    ///     Merge a fresh sample into an existing aggregate. Rolling mean is
    ///     an incremental average; status counts, honeypot hits, and
    ///     dominant client type update in place. Retention priority
    ///     recomputes off the merged state so the shaped-eviction curve
    ///     sees fresh input on every upsert.
    /// </summary>
    public static SessionAggregate Merge(SessionAggregate existing, SessionSample sample)
    {
        var newCount = existing.SampleCount + 1;
        var meanBotProbability =
            (existing.MeanBotProbability * existing.SampleCount + sample.BotProbability) / newCount;
        var maxBotProbability = Math.Max(existing.MaxBotProbability, sample.BotProbability);
        var honeypotHits = existing.HoneypotHits + (sample.Honeypot ? 1 : 0);

        var statusCounts = new Dictionary<int, int>(existing.UpstreamStatusCounts);
        if (sample.FromUpstream)
            statusCounts[sample.StatusCode] = statusCounts.GetValueOrDefault(sample.StatusCode) + 1;

        var dominantClientType = ResolveDominantClientType(existing.DominantClientType, sample.ClientType);

        return existing with
        {
            LastSample = sample.Timestamp,
            SampleCount = newCount,
            MeanBotProbability = meanBotProbability,
            MaxBotProbability = maxBotProbability,
            LatestConfidence = sample.Confidence,
            HoneypotHits = honeypotHits,
            UpstreamStatusCounts = statusCounts,
            DominantClientType = dominantClientType,
            RetentionPriority = ComputeRetentionPriority(
                meanBotProbability,
                sample.Confidence,
                honeypotHits),
        };
    }

    /// <summary>
    ///     Behavioural retention priority. Higher = keep longer under
    ///     pressure. Reflects the invariant in
    ///     <c>reference_session_layer_and_fingerprint_levels</c>:
    ///     "low-confidence still-learning identities stay longer; high-confidence
    ///     stable identities evict first."
    ///
    ///     Bumped hard by honeypot hits -- those are always meaningful and
    ///     never worth evicting during their session window.
    /// </summary>
    /// <param name="meanBotProbability">Aggregate mean bot probability.</param>
    /// <param name="latestConfidence">Confidence of the freshest sample.</param>
    /// <param name="honeypotHits">Cumulative honeypot hits in the session window.</param>
    internal static double ComputeRetentionPriority(
        double meanBotProbability,
        double latestConfidence,
        int honeypotHits)
    {
        // Uncertainty score: 1.0 at p=0.5 (perfectly ambiguous), 0.0 at extremes.
        // A quadratic centred at 0.5 gives a smooth curve peaking on genuine
        // "could go either way" identities.
        var ambiguity = 1.0 - Math.Pow(2.0 * (meanBotProbability - 0.5), 2.0);
        ambiguity = Math.Clamp(ambiguity, 0.0, 1.0);

        // Low confidence multiplies the uncertainty score -- we care most
        // about ambiguous identities where we do not yet trust the verdict.
        var confidenceGap = 1.0 - Math.Clamp(latestConfidence, 0.0, 1.0);
        var learning = ambiguity * confidenceGap;

        // Honeypot hits get a hard floor so honeypot sessions survive
        // pressure even when their aggregate probability landed high (i.e.
        // we are "confident" it is a bot but still want the trail).
        var honeypotFloor = Math.Min(1.0, honeypotHits * 0.5);

        return Math.Max(learning, honeypotFloor);
    }

    private static string? ResolveDominantClientType(string? existing, string? sample)
    {
        if (string.IsNullOrEmpty(sample)) return existing;
        if (string.IsNullOrEmpty(existing)) return sample;
        // Cheap heuristic: latest sample wins when they disagree. The
        // session atom's "did the aggregate shift the fingerprint" test
        // detects the disagreement separately and emits a persistence
        // signal there -- we do not lose the shift by taking the latest.
        return sample;
    }
}
