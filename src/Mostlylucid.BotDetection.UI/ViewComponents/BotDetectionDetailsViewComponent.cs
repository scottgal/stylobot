using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///     <para>
///     Reads the per-signature behavioural clock-axes polygon from a single
///     source of truth: <see cref="SignatureAggregateCache"/>. The cache's
///     <c>LatestSessionVector</c> field is populated write-through by both
///     in-pipeline writers (<c>SessionVectorContributor</c> on wave-30 and
///     <c>SessionAtomizerService</c> on session finalisation) via
///     <c>ISignatureVectorSink</c>. <c>DetectionBroadcastMiddleware</c>
///     pre-seeds a thin aggregate row before the orchestrator runs so the
///     first visit's vector lands cleanly instead of being silently dropped.
///     </para>
///     <para>
///     The dashboard's <c>/api/sessions/signature/{sig}</c> focused-row
///     endpoint reads the same field, so the same signature renders an
///     identical polygon on the home card and on the signature detail page.
///     There is no live-store / persisted-store fallback ladder; if the
///     cache row exists but has no vector yet (post-restart warmup gap,
///     or a brand-new signature whose orchestrator hasn't reached wave-30
///     yet) the view renders a "calibrating" placeholder instead of a
///     polygon derived from a different source.
///     </para>
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly SignatureAggregateCache _cache;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        SignatureAggregateCache cache)
    {
        _extractor = extractor;
        _cache = cache;
    }

    public IViewComponentResult Invoke(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        var primarySig = model.Signatures?.PrimarySignature;
        if (!string.IsNullOrEmpty(primarySig) &&
            _cache.TryGet(primarySig, out var agg) &&
            agg!.LatestSessionVector is { Length: >= 118 } vec)
        {
            var axes = ClockAxesResolver.FromSessionVector(vec);
            if (axes is not null) model = model with { ClockAxes = axes };
        }

        return View(viewName, model);
    }
}
