namespace Mostlylucid.BotDetection.Identity;

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
}
