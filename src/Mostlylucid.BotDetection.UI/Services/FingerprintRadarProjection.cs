using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Projects a <see cref="Fingerprint"/>'s centroid into a small set of named
///     bucket magnitudes suitable for the dashboard's radar polygon. The bucket
///     set matches the natural slot-prefix groupings used by
///     <see cref="IdentityVectorLayout.DefaultV1"/>:
///     <c>Network</c>, <c>Locale</c>, <c>Headers</c>, <c>Tool</c>,
///     <c>Transport</c>, <c>Session</c>, <c>Quality</c>.
///     <para>
///     Per-bucket magnitude = weighted average of the absolute slot values in
///     that bucket, using <c>effective_weight = global ⊙ per_fp</c> as the
///     weighting. Output is in <c>[0, 1]</c> per bucket independent of how many
///     slots that bucket occupies, so a bucket with 4 slots and a bucket with
///     12 slots project to comparable magnitudes.
///     </para>
///     <para>
///     When an archetype origin is passed, its centroid is projected through
///     THE SAME effective weights so the overlay polygon is directly
///     comparable to the current one. The session effect -- how the actor's
///     fingerprint has drifted from its archetype seed -- shows as the gap
///     between the two polygons.
///     </para>
/// </summary>
public static class FingerprintRadarProjection
{
    /// <summary>
    ///     Stable bucket order. Radar axes are rendered in this order.
    /// </summary>
    public static readonly string[] BucketLabels =
        ["Network", "Locale", "Headers", "Tool", "Transport", "Session", "Quality"];

    /// <summary>
    ///     Slot-name prefixes (split on '.') that map into each bucket. Order
    ///     matches <see cref="BucketLabels"/>.
    /// </summary>
    private static readonly string[] BucketPrefixes =
        ["network", "locale", "hdr", "tool", "transport", "session", "quality"];

    public static FingerprintRadarShape Project(
        Fingerprint fingerprint,
        IdentityArchetype? archetype,
        IdentityVectorLayout layout,
        float[] effectiveWeights)
    {
        if (fingerprint.Centroid.Length != layout.Dimension
            || effectiveWeights.Length != layout.Dimension)
        {
            // Layout/vector mismatch is a hard data integrity issue (a vector
            // saved under a different layout version reached this code path).
            // Return a zero shape rather than throwing -- the dashboard will
            // render the calibrating placeholder and the operator can dig
            // into logs. The matcher's layout-version guard should make this
            // unreachable in normal operation.
            return new FingerprintRadarShape(
                CurrentBuckets: new double[BucketLabels.Length],
                OriginBuckets: null,
                BucketLabels: BucketLabels,
                ArchetypeName: null);
        }

        var current = ProjectVector(fingerprint.Centroid, layout, effectiveWeights);
        double[]? origin = null;
        if (archetype is not null && archetype.Centroid.Length == layout.Dimension)
        {
            origin = ProjectVector(archetype.Centroid, layout, effectiveWeights);
        }

        return new FingerprintRadarShape(
            CurrentBuckets: current,
            OriginBuckets: origin,
            BucketLabels: BucketLabels,
            ArchetypeName: archetype?.Name);
    }

    private static double[] ProjectVector(
        float[] vector,
        IdentityVectorLayout layout,
        float[] effectiveWeights)
    {
        var buckets = new double[BucketLabels.Length];
        var weightSums = new double[BucketLabels.Length];

        foreach (var slot in layout.Slots)
        {
            var bucketIdx = BucketIndex(slot.Name);
            if (bucketIdx < 0) continue;

            for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
            {
                var w = Math.Abs(effectiveWeights[i]);
                buckets[bucketIdx] += Math.Abs(vector[i]) * w;
                weightSums[bucketIdx] += w;
            }
        }

        // Normalise by the weight sum so the magnitude is the
        // weighted-average slot magnitude in that bucket, not a sum scaled
        // by bucket size. Result is in [0, 1] because individual slot
        // values from the layout encoder are bounded in [-1, 1].
        for (var b = 0; b < buckets.Length; b++)
        {
            if (weightSums[b] > 0) buckets[b] /= weightSums[b];
            // Clamp defensively in case the encoder ever produces a value
            // outside the [-1, 1] contract.
            if (buckets[b] < 0) buckets[b] = 0;
            if (buckets[b] > 1) buckets[b] = 1;
        }

        return buckets;
    }

    private static int BucketIndex(string slotName)
    {
        var dot = slotName.IndexOf('.');
        if (dot <= 0) return -1;
        var prefix = slotName[..dot];
        for (var i = 0; i < BucketPrefixes.Length; i++)
        {
            if (string.Equals(prefix, BucketPrefixes[i], StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}

/// <summary>
///     Per-bucket projection of a fingerprint's centroid plus its archetype
///     origin (if known). Drives the dashboard's behavioural-fingerprint radar.
/// </summary>
public sealed record FingerprintRadarShape(
    double[] CurrentBuckets,
    double[]? OriginBuckets,
    string[] BucketLabels,
    string? ArchetypeName);
