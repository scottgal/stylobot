namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     One named slot's drift score from the matched archetype's centroid, plus the slot's
///     coarse category prefix (the substring before the first '.'). Returned by
///     <see cref="IdentityWeightMath.TopDriftSlot"/>.
/// </summary>
internal readonly record struct DriftResult(string SlotName, double Score, string Category);

/// <summary>
///     Pure-function helpers for the per-fingerprint weight vector. Both learning signals
///     (corrections and stability) call through here so the renormalisation and clamp rules are
///     applied identically. See docs/architecture/fingerprint-match.md.
/// </summary>
internal static class IdentityWeightMath
{
    /// <summary>
    ///     Apply the correction learning rule. The differentiator vector is per-dim
    ///     (V - L1.centroid)² - (V - L2.centroid)²; positive entries mean dim i discriminated
    ///     L2 from L1, so L2's weight on that dim gets nudged up.
    /// </summary>
    public static void ApplyCorrection(
        float[] weights,
        float[] differentiator,
        double learningRate)
    {
        for (var i = 0; i < weights.Length && i < differentiator.Length; i++)
        {
            if (differentiator[i] > 0)
                weights[i] += (float)(learningRate * differentiator[i]);
        }
    }

    /// <summary>
    ///     Apply the stability learning rule. Per-dim deviation between the absorbed observation
    ///     and the centroid: small deviation = stable for this fingerprint = boost weight.
    ///     stability_i = 1 / (1 + |obs - centroid|); weight nudge is centred at 0.5 stability so
    ///     dims that exactly match the centroid push up, dims that diverge push down.
    /// </summary>
    public static void ApplyStability(
        float[] weights,
        float[] observation,
        float[] centroid,
        double learningRate)
    {
        for (var i = 0; i < weights.Length && i < observation.Length && i < centroid.Length; i++)
        {
            var deviation = Math.Abs(observation[i] - centroid[i]);
            var stability = 1.0 / (1.0 + deviation);
            weights[i] += (float)(learningRate * (stability - 0.5));
        }
    }

    /// <summary>
    ///     Renormalise so the weight vector has mean 1.0, then clamp to [min, max] for numeric
    ///     stability. The clamp prevents any single dim from dominating; renormalisation keeps
    ///     total weight stable across many learning events.
    /// </summary>
    public static void RenormaliseAndClamp(float[] weights, double minWeight, double maxWeight)
    {
        if (weights.Length == 0) return;

        double sum = 0;
        foreach (var w in weights) sum += w;
        if (sum <= 0) return;

        var scale = weights.Length / sum;
        for (var i = 0; i < weights.Length; i++)
            weights[i] = (float)Math.Clamp(weights[i] * scale, minWeight, maxWeight);
    }

    /// <summary>
    ///     Compute the differentiator blob used by the correction learning rule and stored on
    ///     fingerprint_corrections rows.
    /// </summary>
    public static float[] ComputeDifferentiator(float[] vector, float[] l1Centroid, float[] l2Centroid)
    {
        var dim = vector.Length;
        var diff = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            var dl1 = vector[i] - l1Centroid[i];
            var dl2 = vector[i] - l2Centroid[i];
            diff[i] = dl1 * dl1 - dl2 * dl2;
        }
        return diff;
    }

    /// <summary>
    ///     Compute global per-dim weights from cluster-grouped fingerprint centroids using the
    ///     Fisher discriminant ratio: between-cluster variance over within-cluster variance. High
    ///     ratio means the dim varies more across clusters than within them — it discriminates
    ///     identity classes (e.g. real-browser vs scraper). Low ratio means the dim is noise.
    ///
    ///     The result is normalised to mean 1.0 so it composes multiplicatively with per-fp
    ///     weights without inflating the cosine score, then clamped for numeric stability.
    ///
    ///     Returns null when there's not enough data — fewer than 2 clusters with at least one
    ///     fingerprint each, or fewer than <paramref name="minFingerprints"/> fingerprints total.
    /// </summary>
    public static float[]? ComputeFisherWeights(
        IReadOnlyList<(string ClusterId, float[] Centroid)> clusterMembers,
        int dimension,
        int minFingerprints,
        double minWeight,
        double maxWeight,
        double epsilon = 1e-9)
    {
        if (clusterMembers.Count < minFingerprints) return null;

        // Group by cluster id.
        var byCluster = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, centroid) in clusterMembers)
        {
            if (!byCluster.TryGetValue(id, out var list))
            {
                list = new List<float[]>();
                byCluster[id] = list;
            }
            list.Add(centroid);
        }
        if (byCluster.Count < 2) return null;

        // Per-cluster means and global mean per dim.
        var clusterMeans = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var globalMean = new double[dimension];
        foreach (var (id, members) in byCluster)
        {
            var mean = new double[dimension];
            foreach (var c in members)
                for (var d = 0; d < dimension; d++) mean[d] += c[d];
            for (var d = 0; d < dimension; d++) mean[d] /= members.Count;
            clusterMeans[id] = mean;
        }
        var totalCount = clusterMembers.Count;
        foreach (var (_, c) in clusterMembers)
            for (var d = 0; d < dimension; d++) globalMean[d] += c[d];
        for (var d = 0; d < dimension; d++) globalMean[d] /= totalCount;

        // Within-cluster and between-cluster variance per dim.
        var within = new double[dimension];
        var between = new double[dimension];
        foreach (var (id, members) in byCluster)
        {
            var mean = clusterMeans[id];
            foreach (var c in members)
            for (var d = 0; d < dimension; d++)
            {
                var dev = c[d] - mean[d];
                within[d] += dev * dev;
            }
            for (var d = 0; d < dimension; d++)
            {
                var dev = mean[d] - globalMean[d];
                between[d] += members.Count * dev * dev;
            }
        }
        for (var d = 0; d < dimension; d++)
        {
            within[d] /= totalCount;
            between[d] /= totalCount;
        }

        // Fisher ratio + normalise to mean 1.0 + clamp.
        var weights = new float[dimension];
        for (var d = 0; d < dimension; d++)
            weights[d] = (float)(between[d] / (within[d] + epsilon));

        RenormaliseAndClamp(weights, minWeight, maxWeight);
        return weights;
    }

    /// <summary>
    ///     Refine an archetype centroid by blending in the mean of its descendant fingerprints.
    ///     <c>refined = (1 - α) * archetype + α * descendantsMean</c>. The α cap prevents an
    ///     archetype from drifting more than half its identity per cycle even with many
    ///     descendants. Returns null if the descendant list is empty.
    /// </summary>
    public static float[]? RefineArchetypeCentroid(
        float[] archetypeCentroid,
        IReadOnlyList<float[]> descendantCentroids,
        double alphaCap)
    {
        if (descendantCentroids.Count == 0) return null;
        var dim = archetypeCentroid.Length;
        var mean = new float[dim];
        foreach (var c in descendantCentroids)
            for (var d = 0; d < dim; d++) mean[d] += c[d];
        for (var d = 0; d < dim; d++) mean[d] /= descendantCentroids.Count;

        // α scales with descendant count, capped — more descendants = more confident refinement,
        // but never enough to dominate the seed in one cycle.
        var alpha = Math.Min(alphaCap, descendantCentroids.Count / (descendantCentroids.Count + 10.0));
        var refined = new float[dim];
        for (var d = 0; d < dim; d++)
            refined[d] = (float)((1 - alpha) * archetypeCentroid[d] + alpha * mean[d]);
        return refined;
    }

    /// <summary>
    ///     Returns the named layout slot with the largest weighted L2 distance between observed
    ///     and centroid, normalised by slot width so wide LSH slots do not auto-win purely on
    ///     dimension count (especially pre-calibration when all weights are uniform). Per-dim
    ///     contribution is (observed - centroid)² × weight; the slot's total is divided by
    ///     slot.Width.
    ///
    ///     Returns null when vectors are identical, lengths mismatch, or the layout dimension
    ///     does not equal the vector length.
    /// </summary>
    public static DriftResult? TopDriftSlot(
        ReadOnlySpan<float> observed,
        ReadOnlySpan<float> centroid,
        ReadOnlySpan<float> weights,
        IdentityVectorLayout layout)
    {
        if (observed.Length != centroid.Length || observed.Length != weights.Length) return null;
        if (observed.Length != layout.Dimension) return null;

        string? bestSlot = null;
        var bestScore = 0.0;
        foreach (var slot in layout.Slots)
        {
            var score = 0.0;
            for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
            {
                var diff = observed[i] - centroid[i];
                score += diff * diff * weights[i];
            }
            if (slot.Width > 0) score /= slot.Width;
            if (score > bestScore)
            {
                bestScore = score;
                bestSlot = slot.Name;
            }
        }
        if (bestSlot is null) return null;
        var dot = bestSlot.IndexOf('.');
        return new DriftResult(bestSlot, bestScore, dot > 0 ? bestSlot[..dot] : bestSlot);
    }
}
