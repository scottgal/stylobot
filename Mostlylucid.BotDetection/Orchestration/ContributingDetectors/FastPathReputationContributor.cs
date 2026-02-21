using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Ultra-fast Wave 0 contributor that checks for CONFIRMED patterns (both good and bad).
///     Runs FIRST (no dependencies) to enable instant allow/abort for known actors.
///     This is the "12 basic shapes" fast-path check:
///     - Checks patterns that are ConfirmedGood or ManuallyAllowed → instant ALLOW
///     - Checks patterns that are ConfirmedBad or ManuallyBlocked → instant ABORT
///     - Skips Neutral/Suspect patterns (those are handled by ReputationBiasContributor later)
///     - Uses raw UA/IP directly without waiting for signal extraction
///     - Enables circuit-breaker style early exit before expensive analysis
///     Works in tandem with ReputationBiasContributor:
///     - FastPathReputationContributor (Priority 3) - Instant allow/abort for known patterns
///     - ReputationBiasContributor (Priority 45) - Bias for scoring after signals extracted
///
///     Configuration loaded from: fastpath.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:FastPathReputationContributor:*
/// </summary>
public class FastPathReputationContributor : ConfiguredContributorBase
{
    private readonly ILogger<FastPathReputationContributor> _logger;
    private readonly IPatternReputationCache _reputationCache;

    public FastPathReputationContributor(
        ILogger<FastPathReputationContributor> logger,
        IPatternReputationCache reputationCache,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _reputationCache = reputationCache;
    }

    public override string Name => "FastPathReputation";
    public override int Priority => Manifest?.Priority ?? 3;

    // No triggers - runs immediately in Wave 0
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private double FastAbortWeight => GetParam("fast_abort_weight", 3.0);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Fast path: check raw UA and IP against known patterns
        // ConfirmedGood/ManuallyAllowed → instant allow
        // ConfirmedBad/ManuallyBlocked → instant abort

        PatternReputation? matchedPattern = null;
        var matchType = "";
        var isGoodPattern = false;

        // Check raw User-Agent first (most common fast-path hit)
        if (!string.IsNullOrWhiteSpace(state.UserAgent))
        {
            var uaPatternId = CreateUaPatternId(state.UserAgent);
            var uaReputation = _reputationCache.Get(uaPatternId);

            _logger.LogDebug("FastPathReputation UA lookup: pattern={PatternId}, found={Found}, UA={UA}",
                uaPatternId, uaReputation != null, state.UserAgent);

            if (uaReputation?.CanTriggerFastAllow == true)
            {
                matchedPattern = uaReputation;
                matchType = "UserAgent";
                isGoodPattern = true;
            }
            else if (uaReputation?.CanTriggerFastAbort == true)
            {
                matchedPattern = uaReputation;
                matchType = "UserAgent";
                isGoodPattern = false;
            }
        }

        // Check raw IP if UA didn't match
        if (matchedPattern == null && !string.IsNullOrWhiteSpace(state.ClientIp))
        {
            var ipPatternId = CreateIpPatternId(state.ClientIp);
            var ipReputation = _reputationCache.Get(ipPatternId);

            if (ipReputation?.CanTriggerFastAllow == true)
            {
                matchedPattern = ipReputation;
                matchType = "IP";
                isGoodPattern = true;
            }
            else if (ipReputation?.CanTriggerFastAbort == true)
            {
                matchedPattern = ipReputation;
                matchType = "IP";
                isGoodPattern = false;
            }
        }

        // No fast-path match - report neutral so we show in detector list
        if (matchedPattern == null)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[]
            {
                DetectionContribution.Info(Name, "FastPathReputation", "No known patterns in reputation cache")
            });

        // FAST PATH HIT - create instant allow or abort contribution
        if (isGoodPattern) return Task.FromResult(CreateFastAllowContribution(state, matchedPattern, matchType));

        return Task.FromResult(CreateFastAbortContribution(state, matchedPattern, matchType));
    }

    /// <summary>
    ///     Creates a fast-path ALLOW contribution for known good patterns.
    ///     Uses a strong human signal (NOT VerifiedGoodBot early exit) because
    ///     reputation-confirmed "good" patterns are typically real humans, not verified bots.
    ///     VerifiedGoodBot should only be used for cryptographically verified bots (Googlebot, Bingbot etc).
    /// </summary>
    private IReadOnlyList<DetectionContribution> CreateFastAllowContribution(
        BlackboardState state,
        PatternReputation matchedPattern,
        string matchType)
    {
        _logger.LogInformation(
            "Fast-path reputation allow: {PatternId} ({MatchType}) state={State} score={Score:F2} support={Support:F0}",
            matchedPattern.PatternId, matchType, matchedPattern.State, matchedPattern.BotScore, matchedPattern.Support);

        var mtLower = matchType.ToLowerInvariant();
        state.WriteSignals([
            new(SignalKeys.ReputationFastPathHit, true),
            new(SignalKeys.ReputationCanAllow, true),
            new($"reputation.fastpath.{mtLower}.pattern_id", matchedPattern.PatternId),
            new($"reputation.fastpath.{mtLower}.state", matchedPattern.State.ToString()),
            new($"reputation.fastpath.{mtLower}.score", matchedPattern.BotScore),
            new($"reputation.fastpath.{mtLower}.support", matchedPattern.Support)
        ]);

        // Strong human contribution — NOT an early exit.
        // Reputation says this pattern is known-good (likely human), so contribute
        // a strong negative (human) signal that will pull probability down during
        // normal aggregation. Other detectors still run for full coverage.
        var contribution = HumanContribution(
                "FastPathReputation",
                $"Previously verified as safe ({matchType} seen {matchedPattern.Support:F0} times)")
            with
            {
                Weight = 2.5,
                ConfidenceDelta = -0.8
            };

        return new[] { contribution };
    }

    /// <summary>
    ///     Creates a fast-path ABORT contribution for known bad patterns.
    ///     IP patterns use VerifiedBot (early exit) because IPs are specific identifiers.
    ///     UA patterns use a strong bot signal WITHOUT early exit because many legitimate
    ///     users share the same Chrome/Firefox UA string — a UA hash alone is not a
    ///     verified identity, so other detectors must still run to confirm or override.
    /// </summary>
    private IReadOnlyList<DetectionContribution> CreateFastAbortContribution(
        BlackboardState state,
        PatternReputation matchedPattern,
        string matchType)
    {
        _logger.LogWarning(
            "Fast-path reputation abort: {PatternId} ({MatchType}) state={State} score={Score:F2} support={Support:F0}",
            matchedPattern.PatternId, matchType, matchedPattern.State, matchedPattern.BotScore, matchedPattern.Support);

        var mtLower = matchType.ToLowerInvariant();
        state.WriteSignals([
            new(SignalKeys.ReputationFastPathHit, true),
            new(SignalKeys.ReputationCanAbort, true),
            new($"reputation.fastpath.{mtLower}.pattern_id", matchedPattern.PatternId),
            new($"reputation.fastpath.{mtLower}.state", matchedPattern.State.ToString()),
            new($"reputation.fastpath.{mtLower}.score", matchedPattern.BotScore),
            new($"reputation.fastpath.{mtLower}.support", matchedPattern.Support)
        ]);

        // IP patterns: specific identifiers → VerifiedBot early exit is appropriate
        if (matchType == "IP")
        {
            var contribution = DetectionContribution.VerifiedBot(
                    Name,
                    $"Previously identified as bot ({matchType} seen {matchedPattern.Support:F0} times)",
                    botName: matchedPattern.PatternId)
                with
                {
                    ConfidenceDelta = matchedPattern.BotScore,
                    Weight = FastAbortWeight
                };
            return new[] { contribution };
        }

        // UA patterns: shared across many users → strong signal but NO early exit.
        // Other detectors (heuristic, behavioral, header) still run and can override.
        var uaContribution = StrongBotContribution(
                "FastPathReputation",
                $"Previously identified as bot ({matchType} seen {matchedPattern.Support:F0} times)")
            with
            {
                ConfidenceDelta = Math.Min(matchedPattern.BotScore, 0.6),
                Weight = 1.5
            };
        return new[] { uaContribution };
    }

    private static string CreateUaPatternId(string userAgent)
        => PatternNormalization.CreateUaPatternId(userAgent);

    private static string CreateIpPatternId(string ip)
        => PatternNormalization.CreateIpPatternId(ip);
}