using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Foundation SensorAtom (per Taxonomy.md) that classifies the current
///     request into a <c>browser_mode</c> via nearest-centroid cosine over the
///     browser-mode catalogue (production impl:
///     <see cref="CentroidBrowserModeResolver"/>).
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for the legacy
///         <c>BrowserModeClassifierContributor</c>. Reads
///         <see cref="IHttpContextAccessor"/> because the resolver takes the raw
///         <see cref="HttpContext"/> (its own contract) and because the
///         request-scoped items cache lives on the context. Downstream
///         classifier atoms read the emitted signals off the sink, never the
///         context.
///     </para>
///     <para>
///         Same browser, different modes — a real Chrome user produces multiple
///         mode signals across a session (navigation on <c>/</c>, xhr on
///         <c>/api/*</c>, sub-resource on <c>/static/*</c>, signalr-negotiate on
///         <c>/hub/negotiate</c>). This atom only labels the request; the
///         per-mode centroid + access-surface work lives elsewhere.
///     </para>
///     <para>
///         Priority 6 matches the legacy contributor's Wave-0 slot -- runs
///         after <c>IdentityVectorContributor</c>'s raw-values dict is
///         available. Dormant unless
///         <c>Identity.Enabled == true &amp;&amp; Identity.BrowserMode.Enabled == true</c>.
///     </para>
///     <para>
///         See <c>docs/architecture/composite-character-fingerprints.md</c>.
///     </para>
/// </remarks>
public sealed class BrowserModeClassifierAtom : DetectorAtomBase
{
    private readonly ILogger<BrowserModeClassifierAtom> _logger;
    private readonly IBrowserModeResolver _resolver;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _enabled;

    public BrowserModeClassifierAtom(
        ILogger<BrowserModeClassifierAtom> logger,
        IBrowserModeResolver resolver,
        IHttpContextAccessor httpContextAccessor,
        IOptions<BotDetectionOptions> options)
        : base(name: "BrowserModeClassifier", category: "Identity")
    {
        _logger = logger;
        _resolver = resolver;
        _httpContextAccessor = httpContextAccessor;
        // Dormant unless identity is on AND browser-mode is on. Identity off
        // means there is no raw-values signal to classify against.
        _enabled = options.Value.Identity.Enabled && options.Value.Identity.BrowserMode.Enabled;
    }

    public override int Priority => 6;
    public override bool IsEnabled => _enabled;

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        // Single source of truth: the request-scoped resolver. If endpoint
        // policy already classified this request at policy-eval time (composite
        // spec step 5), the HttpContext.Items cache returns the same id with no
        // recomputation; otherwise the resolver computes it now.
        //
        // Spec D2 + feedback_centroids_not_rules: the production resolver is
        // CentroidBrowserModeResolver -- nearest-centroid cosine over the
        // browser_mode catalogue, not a YAML-predicate walk. The atom stays
        // oblivious to which resolver impl is registered so commercial / test
        // hosts can swap behaviour without touching the atom.
        var modeId = _resolver.Resolve(context);
        if (!string.IsNullOrEmpty(modeId))
            sink.Raise($"{SignalKeys.IdentityBrowserMode}:{modeId}", sessionId);

        // Surface the classifier's cosine score when the centroid resolver
        // populated the items entry. Null means a non-centroid resolver was
        // used (test/legacy); leaving the signal absent is the correct shape
        // -- downstream consumers treat the missing signal as "no similarity
        // surfaced" rather than 0.0.
        var similarity = CentroidBrowserModeResolver.TryGetSimilarity(context);
        if (similarity.HasValue)
            sink.Raise(
                $"{SignalKeys.IdentityBrowserModeSimilarity}:{similarity.Value.ToString("F4", CultureInfo.InvariantCulture)}",
                sessionId);

        return Task.FromResult(None());
    }
}