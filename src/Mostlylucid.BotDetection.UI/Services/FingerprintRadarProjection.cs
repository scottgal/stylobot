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

        // No archetype-origin overlay. A sparse archetype (a human browser's Network / Tool /
        // Transport slots are genuinely 0) projects to mostly-zero buckets, which after
        // ScaleToRim draw as spikes radiating from the centre -- the "clock hand" artifact --
        // rather than a meaningful comparison polygon. The radar shows only the current
        // fingerprint shape until there is a non-degenerate overlay design worth rendering.
        ScaleToRim(current);

        return new FingerprintRadarShape(
            CurrentBuckets: current,
            OriginBuckets: null,
            BucketLabels: BucketLabels,
            ArchetypeName: archetype?.Name);
    }

    /// <summary>
    ///     Target radius (fraction of the outer ring) the strongest bucket should
    ///     reach after scaling. Leaves a sliver of headroom so the polygon never
    ///     sits exactly on the ring.
    /// </summary>
    private const double RimTarget = 0.92;

    /// <summary>
    ///     The encoder L2-normalises the identity vector across all ~98 dimensions,
    ///     so a fingerprint's per-bucket weighted-average magnitude lands around
    ///     0.1 -- which the radar (radius x35) would draw as a 3-4px sliver at the
    ///     centre. Rescale so the strongest bucket reaches the rim, using one shared
    ///     divisor across current AND origin so the archetype-drift gap between the
    ///     two polygons stays proportional. This makes the radar a shape/drift
    ///     comparison (its actual purpose) rather than an unreadable absolute plot.
    ///     A genuinely empty shape (all buckets ~0) is left at zero so the partial's
    ///     calibrating placeholder can take over instead of dividing by ~0.
    /// </summary>
    private static void ScaleToRim(double[] buckets)
    {
        var max = 0.0;
        foreach (var v in buckets) if (v > max) max = v;
        if (max <= 1e-6) return;

        var scale = RimTarget / max;
        for (var i = 0; i < buckets.Length; i++) buckets[i] = Math.Min(1.0, buckets[i] * scale);
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
///     <para>
///     <see cref="ArchetypeName"/> is the original assignment from
///     <c>Fingerprint.ArchetypeOrigin</c> -- the prior the matcher anchored
///     the signature at when it was first allocated. <see cref="OriginScore"/>
///     is its cosine against the current centroid (high score = no drift from
///     the prior; low score = the fingerprint has moved a long way since
///     allocation).
///     </para>
///     <para>
///     <see cref="NearestArchetypeName"/> + <see cref="NearestScore"/> are the
///     current-nearest archetype by a fresh <c>FindNearest</c> against the
///     centroid right now. When this differs from <see cref="ArchetypeName"/>
///     it means the behaviour has drifted into a different archetype's
///     neighbourhood -- the operator-visible self-tuning signal. Both can be
///     null when no archetype was assigned or the registry is empty.
///     </para>
/// </summary>
public sealed record FingerprintRadarShape(
    double[] CurrentBuckets,
    double[]? OriginBuckets,
    string[] BucketLabels,
    string? ArchetypeName,
    double? OriginScore = null,
    string? NearestArchetypeName = null,
    double? NearestScore = null,
    // Phase 1 adaptation-loop drift: cosine of the live centroid against the
    // CURRENT active root_centroid (archetype seed at allocation, replaced by
    // BotClusterService community means later). SelfDriftScore = 1.0 immediately
    // after a root reseat, grows toward 0 as the live centroid moves away from
    // the snapshot. SelfDriftLabel is the lineage marker (archetype:... /
    // cluster:... / verifiedbot:... / bootstrap) so operators can tell whether
    // the current root is a cold-start seed or a data-driven community mean.
    // RootHistory is the timeline of past roots in newest-first order; each
    // entry's RootDistance is cosine(live, that historical root) so the
    // dashboard can render "how far have I moved since THAT past anchor" at a
    // glance. Empty for legacy rows that haven't yet been re-rooted post-backfill.
    double? SelfDriftScore = null,
    string? SelfDriftLabel = null,
    DateTime? RootCentroidAt = null,
    IReadOnlyList<RootHistoryView>? RootHistory = null);

/// <summary>
///     One entry in <see cref="FingerprintRadarShape.RootHistory"/>: a past
///     <c>fingerprint_root_history</c> row collapsed for view-side rendering.
///     <see cref="RootDistance"/> is <c>1 - cosine(live, historical root)</c> --
///     0 means the live centroid still matches that past anchor, 1 means it has
///     drifted completely away.
/// </summary>
public sealed record RootHistoryView(
    string Source,
    int MemberCount,
    DateTime SetAt,
    DateTime? SupersededAt,
    double RootDistance);

/// <summary>
///     View-side cosine helper. The Identity layer's BruteForceIdentityAnchorIndex.Cosine
///     is internal to the BotDetection assembly; this is the public counterpart the
///     UI project can call without InternalsVisibleTo. Returns 1.0 for parallel
///     vectors, 0.0 for orthogonal. Tolerates the +1e-12 guard against zero
///     vectors that the index uses.
/// </summary>
public static class FingerprintDriftMath
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0.0;
        double dot = 0, an = 0, bn = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            an  += a[i] * a[i];
            bn  += b[i] * b[i];
        }
        return dot / (Math.Sqrt(an) * Math.Sqrt(bn) + 1e-12);
    }

    /// <summary>1 - cosine, clamped to [0, 1]; the "drift distance" the dashboard renders.</summary>
    public static double Distance(float[] live, float[] root)
    {
        var c = Cosine(live, root);
        if (c < 0) return 1.0;
        if (c > 1) return 0.0;
        return 1.0 - c;
    }
}
