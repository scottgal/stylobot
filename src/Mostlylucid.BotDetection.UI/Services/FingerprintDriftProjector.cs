using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Models;
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

    /// <summary>
    ///     Enrich a batch of visitor rows with their drift badges using the
    ///     fingerprint reader + identity options resolved off
    ///     <paramref name="services"/>. Every visitor-list render path (dashboard
    ///     shell, HTMX partial swap, widget batch, ViewComponent invocation)
    ///     calls this so the badge surfaces consistently. Silent no-op when
    ///     <see cref="IFingerprintReader"/> isn't registered (remote-mode
    ///     dashboard host), <see cref="BotDetectionOptions"/> isn't bound, or
    ///     the threshold is non-positive. Per spec D4 every read recomputes;
    ///     there is no private cache.
    /// </summary>
    public static async Task EnrichVisitorsAsync(
        IReadOnlyList<ProjectedVisitor> visitors,
        IServiceProvider services,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        if (visitors.Count == 0) return;

        var reader = services.GetService<IFingerprintReader>();
        if (reader is null) return;

        var idOpts = services.GetService<IOptions<BotDetectionOptions>>()?.Value.Identity;
        if (idOpts is null) return;
        var threshold = idOpts.Drift.DriftBadgeThreshold;
        if (threshold <= 0) return;

        var layout = services.GetService<IdentityVectorLayout>();
        var signalCatalog = services.GetService<ISignalCatalog>();

        foreach (var v in visitors)
        {
            if (string.IsNullOrEmpty(v.PrimarySignature)) continue;
            try
            {
                var fpId = await reader.LookupFingerprintIdAsync(v.PrimarySignature, ct);
                if (string.IsNullOrEmpty(fpId)) continue;

                var fp = await reader.GetFingerprintAsync(fpId, ct);
                if (fp is null) continue;

                v.DriftBadge = Project(fp, threshold, layout, signalCatalog);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex,
                    "Drift badge enrichment failed for visitor signature {Signature}; row renders without badge",
                    v.PrimarySignature);
            }
        }
    }

    /// <summary>
    ///     Resolve the drift badge for a single signature using the fingerprint
    ///     reader + identity options off <paramref name="services"/>. The
    ///     signature-detail header and any other single-row surface call this
    ///     so the badge contract stays identical across surfaces. Returns null
    ///     under the same conditions <see cref="EnrichVisitorsAsync"/> early-
    ///     returns: identity DI missing, threshold non-positive, no fingerprint
    ///     binding, no usable root centroid, or the drift magnitude is below
    ///     threshold.
    /// </summary>
    public static async Task<DriftBadgeModel?> ResolveForSignatureAsync(
        string primarySignature,
        IServiceProvider services,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(primarySignature)) return null;

        var reader = services.GetService<IFingerprintReader>();
        if (reader is null) return null;

        var idOpts = services.GetService<IOptions<BotDetectionOptions>>()?.Value.Identity;
        if (idOpts is null) return null;
        var threshold = idOpts.Drift.DriftBadgeThreshold;

        var layout = services.GetService<IdentityVectorLayout>();
        var signalCatalog = services.GetService<ISignalCatalog>();

        try
        {
            var fpId = await reader.LookupFingerprintIdAsync(primarySignature, ct);
            if (string.IsNullOrEmpty(fpId)) return null;

            var fp = await reader.GetFingerprintAsync(fpId, ct);
            if (fp is null) return null;

            return Project(fp, threshold, layout, signalCatalog);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "Drift badge lookup failed for {Signature}; surface renders without drift badge",
                primarySignature);
            return null;
        }
    }
}
