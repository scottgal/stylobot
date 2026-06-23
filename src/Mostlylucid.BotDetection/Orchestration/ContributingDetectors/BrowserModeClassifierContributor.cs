using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Foundation contributor that reads the raw-values dict published by
///     <see cref="IdentityVectorContributor"/> plus a thin HttpContext probe,
///     walks the <see cref="BrowserModeRegistry"/>, and emits
///     <c>identity.browser_mode</c> on the blackboard. No persistence, no
///     vector mutation; downstream consumers (mode-aware match, dashboard,
///     endpoint policies) read the signal.
///
///     Same browser, different modes — a real Chrome user produces multiple
///     mode signals across a session (navigation on /, xhr on /api/*,
///     sub-resource on /static/*, signalr-negotiate on /hub/negotiate). The
///     classifier here only labels the request; the per-mode centroid + access
///     surface work lands in later build steps.
///
///     See <c>docs/architecture/composite-character-fingerprints.md</c>.
/// </summary>
public sealed class BrowserModeClassifierContributor : ContributingDetectorBase, IFoundationContributor
{
    private readonly ILogger<BrowserModeClassifierContributor> _logger;
    private readonly IBrowserModeResolver _resolver;
    private readonly bool _enabled;

    public BrowserModeClassifierContributor(
        ILogger<BrowserModeClassifierContributor> logger,
        IBrowserModeResolver resolver,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _resolver = resolver;
        // Dormant unless identity is on AND browser-mode is on. Identity off
        // means there is no raw-values signal to classify against.
        _enabled = options.Value.Identity.Enabled && options.Value.Identity.BrowserMode.Enabled;
    }

    public override string Name => "BrowserModeClassifier";
    public override int Priority => 6;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();
    public override bool IsEnabled => _enabled;

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Single source of truth: the request-scoped resolver. If endpoint
        // policy already classified this request at policy-eval time (composite
        // spec step 5), the HttpContext.Items cache returns the same id with no
        // recomputation; otherwise the resolver computes it now.
        //
        // Spec D2 + feedback_centroids_not_rules: the production resolver is
        // CentroidBrowserModeResolver -- nearest-centroid cosine over the
        // browser_mode catalogue, not a YAML-predicate walk. The contributor
        // stays oblivious to which resolver impl is registered so commercial
        // / test hosts can swap behaviour without touching the contributor.
        var modeId = _resolver.Resolve(state.HttpContext);
        state.WriteSignal(SignalKeys.IdentityBrowserMode, modeId);

        // Surface the classifier's cosine score when the centroid resolver
        // populated the items entry. Null means a non-centroid resolver was
        // used (test/legacy); leaving the signal absent is the correct shape
        // -- downstream consumers treat the missing signal as "no similarity
        // surfaced" rather than 0.0.
        var similarity = CentroidBrowserModeResolver.TryGetSimilarity(state.HttpContext);
        if (similarity.HasValue)
            state.WriteSignal(SignalKeys.IdentityBrowserModeSimilarity, similarity.Value);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
    }
}
