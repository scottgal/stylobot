namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     The six drift signals extracted from Markov chain comparison.
///     Each measures a different dimension of behavioral divergence.
/// </summary>
public readonly record struct DriftSignals
{
    /// <summary>JS divergence between signature's recent chain vs historical chain. Range [0, 1].</summary>
    public double SelfDrift { get; init; }

    /// <summary>JS divergence between signature's chain vs cohort baseline. Range [0, 1].</summary>
    public double HumanDrift { get; init; }

    /// <summary>Fraction of edges in last N transitions that don't exist in historical chain. Range [0, 1].</summary>
    public double TransitionNovelty { get; init; }

    /// <summary>Change in path entropy over time. Positive = more chaotic, negative = more robotic.</summary>
    public double EntropyDelta { get; init; }

    /// <summary>Fraction of recent transitions that are A→B→A→B cycles. Range [0, 1].</summary>
    public double LoopScore { get; init; }

    /// <summary>Average -log P(transition) under cohort baseline for last N transitions. Higher = more surprising.</summary>
    public double SequenceSurprise { get; init; }

    /// <summary>True if any drift signal exceeds its threshold.</summary>
    public bool HasSignificantDrift(MarkovOptions options) =>
        SelfDrift > options.SelfDriftThreshold ||
        HumanDrift > options.HumanDriftThreshold ||
        LoopScore > options.LoopScoreThreshold ||
        SequenceSurprise > options.SequenceSurpriseThreshold;

    public static DriftSignals Empty => new();
}

/// <summary>
///     Computes divergence measures between transition distributions.
/// </summary>
public static class DivergenceMetrics
{
    /// <summary>
    ///     Jensen-Shannon divergence between two distributions.
    ///     Bounded [0, 1], symmetric, stable with zero probabilities.
    /// </summary>
    public static double JensenShannonDivergence(
        Dictionary<string, double> p,
        Dictionary<string, double> q)
    {
        if (p.Count == 0 && q.Count == 0) return 0;
        if (p.Count == 0 || q.Count == 0) return 1;

        // Get union of all keys
        var allKeys = new HashSet<string>(p.Keys);
        allKeys.UnionWith(q.Keys);

        // Compute M = (P + Q) / 2
        var m = new Dictionary<string, double>();
        foreach (var key in allKeys)
        {
            var pVal = p.GetValueOrDefault(key, 0);
            var qVal = q.GetValueOrDefault(key, 0);
            m[key] = (pVal + qVal) / 2.0;
        }

        // JSD = (KL(P||M) + KL(Q||M)) / 2
        var klPm = KlDivergence(p, m, allKeys);
        var klQm = KlDivergence(q, m, allKeys);

        return Math.Clamp((klPm + klQm) / 2.0, 0, 1);
    }

    /// <summary>
    ///     KL divergence from P to Q (with smoothing to avoid log(0)).
    /// </summary>
    private static double KlDivergence(
        Dictionary<string, double> p,
        Dictionary<string, double> q,
        HashSet<string> allKeys)
    {
        const double epsilon = 1e-10;
        var kl = 0.0;

        foreach (var key in allKeys)
        {
            var pVal = p.GetValueOrDefault(key, 0);
            if (pVal <= 0) continue;

            var qVal = Math.Max(q.GetValueOrDefault(key, 0), epsilon);
            kl += pVal * Math.Log2(pVal / qVal);
        }

        return Math.Max(0, kl);
    }

    /// <summary>
    ///     Compute average transition surprise: -log2 P(transition) under a baseline model.
    ///     Higher values = more improbable sequence.
    /// </summary>
    public static double AverageTransitionSurprise(
        IReadOnlyList<(string From, string To)> recentTransitions,
        DecayingTransitionMatrix baseline,
        DateTime now)
    {
        if (recentTransitions.Count == 0) return 0;

        var totalSurprise = 0.0;
        var count = 0;

        foreach (var (from, to) in recentTransitions)
        {
            var prob = baseline.GetTransitionProbability(from, to, now);
            if (prob <= 0)
                totalSurprise += 10.0; // Max surprise for impossible transitions
            else
                totalSurprise += -Math.Log2(prob);
            count++;
        }

        return count > 0 ? totalSurprise / count : 0;
    }

    /// <summary>
    ///     Detect A→B→A→B loop patterns in recent transitions.
    ///     Returns fraction of transitions that participate in 2-cycles.
    /// </summary>
    public static double ComputeLoopScore(IReadOnlyList<(string From, string To)> recentTransitions)
    {
        if (recentTransitions.Count < 4) return 0;

        var loopTransitions = 0;

        for (var i = 2; i < recentTransitions.Count; i++)
        {
            // Check for A→B, B→A pattern
            if (recentTransitions[i].From == recentTransitions[i - 1].To &&
                recentTransitions[i].To == recentTransitions[i - 1].From)
            {
                loopTransitions += 2; // Both transitions participate
            }
        }

        // Deduplicate (each transition counted once)
        return Math.Min(1.0, (double)loopTransitions / (recentTransitions.Count * 2));
    }

    /// <summary>
    ///     Compute transition novelty: fraction of recent edges not seen in historical chain.
    /// </summary>
    public static double ComputeTransitionNovelty(
        IReadOnlyList<(string From, string To)> recentTransitions,
        DecayingTransitionMatrix historicalChain,
        DateTime now)
    {
        if (recentTransitions.Count == 0) return 0;

        var novelCount = recentTransitions.Count(t => !historicalChain.HasEdge(t.From, t.To, now));
        return (double)novelCount / recentTransitions.Count;
    }
}
