using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserChar;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Browser-characteristic consistency (claim verification, NOT fingerprinting).
///     Scores a session's observed feature/engine vector -- from the client beacon,
///     carried on the <see cref="BrowserFingerprintResult"/> -- against the LEARNED
///     browser_char centroid for its CLAIMED <c>{family}:mode</c>. Deviation from the
///     claim (weighted toward the un-spoofable engine dims) is the inconsistency: a
///     session claiming Chrome while running SpiderMonkey/JSC lands high drift and gets
///     a suspicion contribution; a consistent session gets NOTHING (never a human
///     discount -- overview's asymmetry rule).
///
///     <para>
///     Priority 19: runs after <see cref="ClientSideAtom"/> (18) has populated the
///     fingerprint result on <see cref="HttpContext.Items"/>. A TRIGGERED classifier,
///     not foundation: it fails open (no signal) when there is no beacon, no engine
///     observation, or the claimed family has no seeded centroid, so it never blocks on
///     a round-trip that is not coming. Dormant unless
///     <c>Identity.Enabled &amp;&amp; Identity.BrowserChar.Enabled</c>.
///     </para>
/// </summary>
public sealed class BrowserCharConsistencyAtom : DetectorAtomBase
{
    private const string FingerprintItemsKey = "__mlbotd_fingerprint";

    private readonly ILogger<BrowserCharConsistencyAtom> _logger;
    private readonly BrowserCharConsistencyScorer _scorer;
    private readonly IdentityVectorLayout _layout;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _enabled;
    private readonly double _driftThreshold;
    private readonly double _weight;

    public BrowserCharConsistencyAtom(
        ILogger<BrowserCharConsistencyAtom> logger,
        BrowserCharConsistencyScorer scorer,
        IdentityVectorLayout layout,
        IHttpContextAccessor httpContextAccessor,
        IOptions<BotDetectionOptions> options)
        : base(name: "BrowserCharConsistency", category: "Inconsistency")
    {
        _logger = logger;
        _scorer = scorer;
        _layout = layout;
        _httpContextAccessor = httpContextAccessor;
        var id = options.Value.Identity;
        _enabled = id.Enabled && id.BrowserChar.Enabled;
        _driftThreshold = id.BrowserChar.DriftThreshold;
        _weight = id.BrowserChar.Weight;
    }

    public override int Priority => 19;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink, string sessionId, CancellationToken ct = default)
    {
        if (!_enabled) return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());
        if (context.Items[FingerprintItemsKey] is not BrowserFingerprintResult fp) return Task.FromResult(None());

        // Anchor on engine data: the engine dims are the un-spoofable substrate. No
        // engine observation (pre-v2.1.0 beacon / errored probe) -> fail open.
        var engine = fp.Engine;
        if (engine is null || string.IsNullOrEmpty(engine.StackStyle)) return Task.FromResult(None());

        // Claimed family from the UA-family signal; map to a seeded family. Bots and
        // unknown families are not checked here (fail open).
        var family = MapFamily(sink.ReadHint(SignalKeys.UserAgentFamily));
        if (family is null) return Task.FromResult(None());
        var key = family + ":normal";

        var observation = BrowserCharVectorEncoder.Encode(_layout, fp.Features, fp.Triple, engine);
        var score = _scorer.Score(key, observation);
        if (!score.KeyFound) return Task.FromResult(None());

        sink.Raise(
            $"{SignalKeys.BrowserCharacteristicDrift}:{score.Drift.ToString("F3", CultureInfo.InvariantCulture)}",
            sessionId);

        // ASYMMETRIC: consistency (drift at/under threshold) is neutral -- no contribution,
        // never a discount toward human. Only deviation raises suspicion.
        if (score.Drift <= _driftThreshold) return Task.FromResult(None());

        var over = Math.Clamp((score.Drift - _driftThreshold) / Math.Max(1e-6, 1.0 - _driftThreshold), 0, 1);
        var delta = 0.15 + 0.25 * over; // positive only; scales with how far past the threshold

        _logger.LogDebug(
            "Browser-characteristic inconsistency: claimed {Family}, drift {Drift:F2}, cos {Cos:F2}",
            family, score.Drift, score.Consistency);

        IReadOnlyList<DetectionContribution> contributions = new List<DetectionContribution>
        {
            new()
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = delta,
                Weight = _weight,
                Reason =
                    $"Browser-characteristic inconsistency: observed engine/features diverge from "
                    + $"claimed {family} (drift {score.Drift:F2}, weighted cos {score.Consistency:F2})",
            },
        };
        return Task.FromResult(contributions);
    }

    /// <summary>Map a UA-family label to a seeded browser_char family, or null (fail open).</summary>
    private static string? MapFamily(string? uaFamily)
    {
        if (string.IsNullOrEmpty(uaFamily)) return null;
        var f = uaFamily.ToLowerInvariant();
        if (f.Contains("edge")) return "edge";
        if (f.Contains("chrome") || f.Contains("chromium")) return "chrome";
        if (f.Contains("firefox")) return "firefox";
        if (f.Contains("safari")) return "safari";
        return null; // bots (Googlebot / curl / ...) and unknown families are not claim-checked here
    }
}
