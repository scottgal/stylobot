using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
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
    private readonly IdentityGlobalWeightsCache _globalWeights;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        IFingerprintReader fingerprintReader,
        IdentityArchetypeRegistry archetypes,
        IdentityVectorLayout layout,
        IdentityGlobalWeightsCache globalWeights)
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

        if (context?.Items[SignalKeys.IdentityFingerprintId] is string fpId
            && !string.IsNullOrEmpty(fpId))
        {
            try
            {
                var fp = await _fingerprintReader.GetFingerprintAsync(fpId, context.RequestAborted);
                if (fp is not null)
                {
                    var archetype = _archetypes.TryGetById(fp.ArchetypeOrigin);
                    var effectiveWeights = _globalWeights.Compose(fp.Weights);
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
}
