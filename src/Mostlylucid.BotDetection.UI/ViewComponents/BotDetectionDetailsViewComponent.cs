using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///     <para>
///     Read source: the visitor's <see cref="Fingerprint"/> resolved by
///     <see cref="IFingerprintReader.GetFingerprintAsync"/> against the
///     <c>IdentityFingerprintId</c> the orchestrator wrote to
///     <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/>. The
///     fingerprint's centroid (drift-from-archetype state across all
///     absorbed observations) is the canonical render source -- session
///     vectors are an internal orchestrator signal and are not
///     visualised anywhere in the dashboard.
///     </para>
///     <para>
///     Projection: <see cref="FingerprintRadarProjection"/> groups the
///     centroid's slots by their natural buckets (Network, Locale,
///     Headers, Tool, Transport, Session, Quality) and weights each
///     slot by <c>effective_weight = global ⊙ per_fp</c>. The archetype
///     origin (when present on the fingerprint) is projected through
///     the same effective weights, so the overlay polygon shows the
///     drift from seed to current shape -- the "session effect."
///     </para>
///     <para>
///     "Calibrating" placeholder renders only when the orchestrator
///     has not yet assigned an <c>IdentityFingerprintId</c> for this
///     request (genuine pre-archetype-match window, near-zero in normal
///     operation once the matcher has run).
///     </para>
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly IFingerprintReader _fingerprintReader;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityVectorLayout _layout;
    // Optional: detection-side cache. Absent in pure dashboard-viewer hosts
    // (header-driven, no DB). When null, fall back to per-fp weights unchanged --
    // the global multiplier is a refinement, not a prerequisite.
    private readonly IdentityGlobalWeightsCache? _globalWeights;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        IFingerprintReader fingerprintReader,
        IdentityArchetypeRegistry archetypes,
        IdentityVectorLayout layout,
        IdentityGlobalWeightsCache? globalWeights = null)
    {
        _extractor = extractor;
        _fingerprintReader = fingerprintReader;
        _archetypes = archetypes;
        _layout = layout;
        _globalWeights = globalWeights;
    }

    public async Task<IViewComponentResult> InvokeAsync(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        var fpId = ResolveFingerprintId(context);

        // Upstream-trust / cached-verdict fast-paths bypass the orchestrator, so
        // neither HttpContext.Items nor AggregatedEvidence carries the
        // IdentityFingerprintId for those requests. Fall back to a direct
        // signature → fingerprint lookup so the home card still renders on
        // L1 fast-allow paths. PrimarySignature is set by the FOSS detection
        // middleware regardless of which gate fired.
        if (string.IsNullOrEmpty(fpId) && context is not null
            && model.Signatures?.PrimarySignature is { } primarySig
            && !string.IsNullOrEmpty(primarySig))
        {
            try
            {
                fpId = await _fingerprintReader.LookupFingerprintIdAsync(primarySig, context.RequestAborted);
            }
            catch
            {
                // Reader failure -> falls through to calibrating.
            }
        }

        if (!string.IsNullOrEmpty(fpId) && context is not null)
        {
            try
            {
                var fp = await _fingerprintReader.GetFingerprintAsync(fpId, context.RequestAborted);
                if (fp is not null)
                {
                    var archetype = _archetypes.TryGetById(fp.ArchetypeOrigin);
                    // _globalWeights is optional in dashboard-viewer hosts that don't run
                    // detection. When absent the per-fp weights stand in unchanged --
                    // the global multiplier is a refinement, not a prerequisite.
                    var effectiveWeights = _globalWeights?.Compose(fp.Weights) ?? fp.Weights;
                    var shape = FingerprintRadarProjection.Project(fp, archetype, _layout, effectiveWeights);
                    model = model with { FingerprintShape = shape };
                }
            }
            catch
            {
                // Read failure is best-effort -- the view falls through to
                // calibrating. Logging happens inside the reader.
            }
        }

        return View(viewName, model);
    }

    /// <summary>
    ///     Identity fingerprint id is exposed through two channels by the
    ///     detection layer:
    ///     <list type="bullet">
    ///         <item><c>HttpContext.Items[SignalKeys.IdentityFingerprintId]</c>
    ///         when the request hit the gate-bias fast-path with a cached
    ///         verdict (set by <c>BotDetectionMiddleware</c>).</item>
    ///         <item><c>AggregatedEvidence.Signals[SignalKeys.IdentityFingerprintId]</c>
    ///         when the full orchestrator ran (the normal path for any request
    ///         that wasn't an L1 verdict-cache hit).</item>
    ///     </list>
    ///     We check both so the home card renders the fingerprint regardless of
    ///     which path resolved it. Returns null only when neither has the id,
    ///     which means the matcher hasn't produced a fingerprint for this
    ///     request yet (pre-archetype-match window or matcher disabled).
    /// </summary>
    private static string? ResolveFingerprintId(Microsoft.AspNetCore.Http.HttpContext? context)
    {
        if (context is null) return null;

        if (context.Items[SignalKeys.IdentityFingerprintId] is string fastPathId
            && !string.IsNullOrEmpty(fastPathId))
            return fastPathId;

        if (context.Items[BotDetection.Middleware.BotDetectionMiddleware.AggregatedEvidenceKey]
                is AggregatedEvidence evidence
            && evidence.Signals.TryGetValue(SignalKeys.IdentityFingerprintId, out var sigObj)
            && sigObj is string evidenceId
            && !string.IsNullOrEmpty(evidenceId))
            return evidenceId;

        return null;
    }
}
