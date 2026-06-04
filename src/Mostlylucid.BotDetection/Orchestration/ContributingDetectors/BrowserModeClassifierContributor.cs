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
    private readonly BrowserModeRegistry _registry;
    private readonly bool _enabled;

    public BrowserModeClassifierContributor(
        ILogger<BrowserModeClassifierContributor> logger,
        BrowserModeRegistry registry,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _registry = registry;
        // Dormant unless identity is on AND browser-mode is on. Identity off
        // means there is no raw-values signal to classify against.
        _enabled = options.Value.Identity.Enabled && options.Value.Identity.BrowserMode.Enabled;
    }

    public override string Name => "BrowserModeClassifier";

    // Priority 6 — runs in the same foundation wave as IdentityVectorContributor
    // (priority 5). The orchestrator does not gate on priority for the wave;
    // we read raw values from the signal if present and self-compose only if
    // not, the same pattern FingerprintMatchContributor uses for the vector.
    public override int Priority => 6;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();
    public override bool IsEnabled => _enabled;

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?>? raw = null;
        if (state.Signals.TryGetValue(SignalKeys.IdentityRawValues, out var rawObj))
            raw = rawObj as IReadOnlyDictionary<string, object?>;

        // Fallback path: if IdentityVectorContributor lost the wave (e.g.
        // identity-off-but-mode-on misconfig), self-compose. Same pattern the
        // fingerprint matcher uses.
        raw ??= IdentityVectorContributor.ComposeRawValues(state);

        var probe = new HttpContextRequestProbe(state.HttpContext);
        var modeId = _registry.Classify(raw, probe);
        state.WriteSignal(SignalKeys.IdentityBrowserMode, modeId);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
    }

    private sealed class HttpContextRequestProbe : IRequestProbe
    {
        private readonly HttpContext _ctx;

        public HttpContextRequestProbe(HttpContext ctx) => _ctx = ctx;

        public string Method => _ctx.Request.Method ?? string.Empty;
        public string Path => _ctx.Request.Path.Value ?? string.Empty;

        public bool HasHeader(string name) => _ctx.Request.Headers.ContainsKey(name);

        public string HeaderOrDefault(string name, string fallback)
            => _ctx.Request.Headers.TryGetValue(name, out var value)
                ? value.ToString()
                : fallback;
    }
}
