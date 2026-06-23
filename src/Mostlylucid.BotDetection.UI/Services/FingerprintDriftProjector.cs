using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Models.Primitives;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Read-time projector for the operator-visible "drifted" badge that the
///     visitor list + signature detail header render (plan task 19). Combines
///     the existing <see cref="FingerprintDriftMath"/> cosine-distance metric
///     with the per-slot top-K decomposition that <see cref="ModeDeltaProjector"/>
///     uses for the mode-shift panel — same shape ("magnitude + top semantic
///     slot"), different inputs (live centroid vs root centroid, not mode
///     centroid vs identity centroid).
///
///     <para>
///     Per spec D5 (<c>project_gateway_data_locality</c>) the threshold cross
///     and the slot-label resolution happen here on the gateway; the wire
///     payload is the small <see cref="DriftBadgeModel"/> record. Per spec D4
///     (<c>feedback_centralised_change_detection</c>) the projection is pure
///     and re-runs on every SSR / OOB swap — no per-badge cache, no private
///     invalidation.
///     </para>
/// </summary>
public static class FingerprintDriftProjector
{
    /// <summary>
    ///     Build the badge model for a fingerprint. <paramref name="threshold"/>
    ///     is <c>IdentityOptions.Drift.DriftBadgeThreshold</c> (default 0.15).
    ///     The returned model carries the threshold-cross result on
    ///     <see cref="DriftBadgeModel.IsDrifted"/> so the partial can fast-skip
    ///     without re-evaluating; callers may discard the whole model on
    ///     <c>!IsDrifted</c> to keep the view-model payload minimal.
    ///
    ///     <para>
    ///     Returns <c>null</c> when the fingerprint has no usable root
    ///     centroid (legacy migration boundary; runtime steady state always
    ///     populates it) or the live centroid is degenerate (length 0). The
    ///     caller treats null as "no badge data" — distinct from
    ///     <c>IsDrifted=false</c> which means "we evaluated, no badge needed".
    ///     </para>
    /// </summary>
    public static DriftBadgeModel? Project(
        Fingerprint fingerprint,
        double threshold,
        IdentityVectorLayout? layout,
        ISignalCatalog? signalCatalog)
    {
        if (fingerprint.Centroid is null || fingerprint.Centroid.Length == 0)
            return null;

        // Root centroid is the drift anchor. Runtime contract is "never null"
        // (matcher seed + migration backfill both enforce); we still treat
        // it as optional so the projector doesn't throw on a legacy row.
        var rootVec = fingerprint.RootCentroid;
        if (rootVec is null || rootVec.Length != fingerprint.Centroid.Length)
            return null;

        var magnitude = FingerprintDriftMath.Distance(fingerprint.Centroid, rootVec);
        var isDrifted = magnitude > threshold;

        // Top-slot identification mirrors ModeDeltaProjector: group per-dim
        // deltas by their owning layout slot, pick the slot with the largest
        // per-slot L2. Skip when layout is missing (thin remote-mode host)
        // or the two arrays have different lengths (defensive).
        string topSlotLabel = string.Empty;
        if (layout is not null && fingerprint.Centroid.Length == rootVec.Length)
        {
            string? topKey = null;
            double topSlotSq = 0;
            foreach (var slot in layout.Slots)
            {
                double slotSq = 0;
                var end = Math.Min(slot.Offset + slot.Width, fingerprint.Centroid.Length);
                for (var i = slot.Offset; i < end; i++)
                {
                    var d = (double)fingerprint.Centroid[i] - rootVec[i];
                    slotSq += d * d;
                }
                if (slotSq > topSlotSq)
                {
                    topSlotSq = slotSq;
                    topKey = slot.Name;
                }
            }
            if (topKey is not null)
                topSlotLabel = SlotKeyLabel.ResolveOrNormalize(topKey, signalCatalog);
        }

        return new DriftBadgeModel(
            IsDrifted: isDrifted,
            Magnitude: magnitude,
            OriginArchetypeId: fingerprint.ArchetypeOrigin ?? "—",
            TopSlotLabel: topSlotLabel);
    }
}
