using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     The outcome of folding one observation into a fingerprint's compact
///     delta-streaming state: the four delta fields (<c>delta_from_archetype</c> /
///     <c>delta_archetype_id</c> / <c>delta_count</c> / <c>novelty_count</c>).
///     A pure value — no DB, no store state — so every store folds identically.
/// </summary>
public readonly record struct FingerprintDeltaFold(
    float[]? DeltaFromArchetype,
    string? DeltaArchetypeId,
    int DeltaCount,
    int NoveltyCount);

/// <summary>
///     DB-agnostic delta-streaming absorption math, shared by every concrete
///     <see cref="IFingerprintStore"/> (the FOSS SQLite store and the commercial
///     Postgres mirror) so the Mahalanobis novelty gate, the running-mean compact-delta
///     accumulation and the detached sampler's dirty-selection predicate are defined ONCE
///     in the FOSS core and called identically by both stores. Pure Fingerprint→Fingerprint
///     math + options + archetype registry — no DB surface here.
///
///     <para>
///     Dormant by default: when <see cref="DeltaNoveltyOptions.Enabled"/> is false every
///     method is a no-op / fail-open, so fingerprint evolution is byte-identical to the
///     pre-delta model until an operator flips the switch.
///     </para>
///
///     <para>
///     Why a PUBLIC class here rather than <c>internal</c> like
///     <see cref="IdentityWeightMath"/>: the commercial Postgres store is a SEPARATE
///     assembly with no <c>InternalsVisibleTo</c>, and it must call this same code. The
///     visibility mirrors the other shared public FOSS types it already consumes
///     (<see cref="Fingerprint"/>, <see cref="IdentityArchetypeRegistry"/>,
///     <see cref="SessionVectorizer"/>).
///     </para>
/// </summary>
public static class IdentityDeltaMath
{
    /// <summary>
    ///     The delta-streaming novelty gate. Computes the covariance-normalised Mahalanobis
    ///     distance from an observation vector to its nearest archetype centroid and decides
    ///     whether the observation is a confirmatory member (within threshold — the compact
    ///     delta is accumulated) or genuinely novel (beyond threshold — full detail + seed
    ///     consideration). NOT cosine/Euclidean: the distance is normalised by the archetype's
    ///     per-dimension variance (<see cref="IdentityArchetypeRegistry.EffectiveVarianceFor"/>),
    ///     so a deviation on a low-variance dimension (one the archetype "really means") counts
    ///     as more novel than the same deviation on a loosely-asserted dimension.
    ///
    ///     Returns the Mahalanobis distance when the gate is enabled and a compatible
    ///     archetype/variance exists; null when the gate is disabled, the registry is absent
    ///     (minimal hosts), or the archetype's dimension count mismatches the vector. The
    ///     caller treats null as "not novel" (fail-open) so a disabled or misconfigured gate
    ///     never changes fingerprint evolution.
    /// </summary>
    public static double? ComputeNoveltyDistance(
        float[] vec,
        IdentityArchetype? archetype,
        IdentityArchetypeRegistry? archetypes,
        DeltaNoveltyOptions options)
    {
        if (!options.Enabled) return null;
        if (vec is null || archetype is null) return null;
        if (archetypes is null) return null; // minimal host: no reference centroids to gate against

        // Gate against the archetype's unit-length Centroid (the observation vector is
        // L2-normalised too); CentroidRaw is pre-normalisation and fp.Centroid is a seed
        // blend — neither is a comparable reference. Dimension mismatch → fail-open null.
        if (vec.Length != archetype.Centroid.Length) return null;

        var variance = archetypes.EffectiveVarianceFor(archetype);
        if (variance.Length != vec.Length) return null;

        return SessionVectorizer.MahalanobisDistance(vec, archetype.Centroid, variance);
    }

    /// <summary>
    ///     True when the observation is beyond the Mahalanobis novelty threshold (genuinely
    ///     novel); false when within threshold (confirmatory), when the gate is disabled, or
    ///     when no compatible archetype/variance exists (fail-open). See
    ///     <see cref="ComputeNoveltyDistance"/>.
    /// </summary>
    public static bool IsNovelObservation(
        float[] vec,
        IdentityArchetype? archetype,
        IdentityArchetypeRegistry? archetypes,
        DeltaNoveltyOptions options)
    {
        var distance = ComputeNoveltyDistance(vec, archetype, archetypes, options);
        return distance is not null && distance.Value >= options.MahalanobisNoveltyThreshold;
    }

    /// <summary>
    ///     Fold one observation into the fingerprint's compact delta state. When the gate is
    ///     enabled, resolves the nearest archetype to THIS observation (the spec's "find
    ///     nearest centroid" is per-encounter, against the observation — not the folded
    ///     centroid, which is a smoothed blend) and gates it by Mahalanobis distance. Within
    ///     threshold → confirmatory: accumulate the compact running-mean delta
    ///     (obs − archetype centroid) and its count. Beyond threshold → genuinely novel: bump
    ///     the novelty count (the periodic Leiden consolidator decides seeding; never
    ///     per-request). Gate disabled / no registry / dimension mismatch → the delta fields
    ///     come back unchanged (fail-open), so the default-disabled path is byte-identical to
    ///     pre-delta.
    /// </summary>
    public static FingerprintDeltaFold FoldObservation(
        Fingerprint fp,
        float[] vec,
        string? uaFamily,
        IdentityArchetypeRegistry? archetypes,
        DeltaNoveltyOptions options)
    {
        var newDeltaFromArchetype = fp.DeltaFromArchetype;
        var newDeltaArchetypeId = fp.DeltaArchetypeId;
        var newDeltaCount = fp.DeltaCount;
        var newNoveltyCount = fp.NoveltyCount;
        if (options.Enabled && archetypes is not null)
        {
            var anchor = archetypes.FindNearest(vec, uaFamily);
            if (anchor is not null
                && anchor.Archetype.Centroid.Length == vec.Length
                && ComputeNoveltyDistance(vec, anchor.Archetype, archetypes, options) is { } distance)
            {
                if (distance < options.MahalanobisNoveltyThreshold)
                {
                    // Confirmatory: fold the observation into the running-mean delta.
                    // delta_new = (delta * count + (obs − centroid)) / (count + 1).
                    var existing = newDeltaFromArchetype;
                    var count = newDeltaCount;
                    var updated = new float[vec.Length];
                    for (var i = 0; i < vec.Length; i++)
                    {
                        var residual = vec[i] - anchor.Archetype.Centroid[i];
                        updated[i] = existing is not null && existing.Length == vec.Length && count > 0
                            ? (existing[i] * count + residual) / (count + 1)
                            : residual;
                    }
                    newDeltaFromArchetype = updated;
                    newDeltaArchetypeId = anchor.Archetype.ArchetypeId;
                    newDeltaCount = count + 1;
                }
                else
                {
                    // Genuinely novel — do NOT smear the delta with an out-of-catchment
                    // point. Flag it for the Leiden consolidator.
                    newNoveltyCount = fp.NoveltyCount + 1;
                }
            }
        }

        return new FingerprintDeltaFold(
            newDeltaFromArchetype, newDeltaArchetypeId, newDeltaCount, newNoveltyCount);
    }

    /// <summary>
    ///     The detached sampler's dirty-selection predicate: true when the fingerprint's
    ///     in-memory delta / novelty state has advanced past the sampler-owned watermark of
    ///     what the durable row already holds (the two ints threaded on the resident cache
    ///     entry). Nothing on the request path marks the fingerprint — the fold only advances
    ///     the record's fields; this predicate is how the detached pass decides what to write.
    /// </summary>
    public static bool DeltaAdvancedPastWatermark(
        int deltaCount, int noveltyCount, int persistedDeltaCount, int persistedNoveltyCount)
        => deltaCount != persistedDeltaCount || noveltyCount != persistedNoveltyCount;
}
