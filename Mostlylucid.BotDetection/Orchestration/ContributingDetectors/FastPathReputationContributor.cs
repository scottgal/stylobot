using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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
        if (isGoodPattern) return Task.FromResult(CreateFastAllowContribution(matchedPattern, matchType));

        return Task.FromResult(CreateFastAbortContribution(matchedPattern, matchType));
    }

    /// <summary>
    ///     Creates a fast-path ALLOW contribution for known good patterns.
    /// </summary>
    private IReadOnlyList<DetectionContribution> CreateFastAllowContribution(
        PatternReputation matchedPattern,
        string matchType)
    {
        _logger.LogInformation(
            "Fast-path reputation allow: {PatternId} ({MatchType}) state={State} score={Score:F2} support={Support:F0}",
            matchedPattern.PatternId, matchType, matchedPattern.State, matchedPattern.BotScore, matchedPattern.Support);

        var signals = ImmutableDictionary<string, object>.Empty
            .Add(SignalKeys.ReputationFastPathHit, true)
            .Add(SignalKeys.ReputationCanAllow, true)
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.pattern_id", matchedPattern.PatternId)
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.state", matchedPattern.State.ToString())
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.score", matchedPattern.BotScore)
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.support", matchedPattern.Support);

        // Derive a friendly bot name from the pattern
        var botName = matchedPattern.PatternId.StartsWith("ua:")
            ? "Verified Good Bot"
            : $"Trusted IP ({matchedPattern.PatternId})";

        var contribution = DetectionContribution.VerifiedGoodBot(
                Name,
                $"[FastPath] Known good {matchType}: {matchedPattern.State} (score={matchedPattern.BotScore:F2}, support={matchedPattern.Support:F0})",
                botName)
            with
            {
                Signals = signals
            };

        return new[] { contribution };
    }

    /// <summary>
    ///     Creates a fast-path ABORT contribution for known bad patterns.
    /// </summary>
    private IReadOnlyList<DetectionContribution> CreateFastAbortContribution(
        PatternReputation matchedPattern,
        string matchType)
    {
        _logger.LogWarning(
            "Fast-path reputation abort: {PatternId} ({MatchType}) state={State} score={Score:F2} support={Support:F0}",
            matchedPattern.PatternId, matchType, matchedPattern.State, matchedPattern.BotScore, matchedPattern.Support);

        var signals = ImmutableDictionary<string, object>.Empty
            .Add(SignalKeys.ReputationFastPathHit, true)
            .Add(SignalKeys.ReputationCanAbort, true)
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.pattern_id", matchedPattern.PatternId)
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.state", matchedPattern.State.ToString())
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.score", matchedPattern.BotScore)
            .Add($"reputation.fastpath.{matchType.ToLowerInvariant()}.support", matchedPattern.Support);

        var contribution = DetectionContribution.VerifiedBot(
                Name,
                $"[FastPath] Known bad {matchType}: {matchedPattern.State} (score={matchedPattern.BotScore:F2}, support={matchedPattern.Support:F0})",
                botName: matchedPattern.PatternId)
            with
            {
                ConfidenceDelta = matchedPattern.BotScore,
                Weight = FastAbortWeight, // Very high weight for confirmed patterns - instant abort
                Signals = signals
            };

        return new[] { contribution };
    }

    /// <summary>
    ///     Create pattern ID for User-Agent using simple normalization.
    ///     Fast path uses simplified normalization for speed.
    /// </summary>
    private static string CreateUaPatternId(string userAgent)
    {
        // Simple normalization for fast path matching
        var normalized = NormalizeForFastPath(userAgent);
        var hash = ComputeHash(normalized);
        return $"ua:{hash}";
    }

    /// <summary>
    ///     Create pattern ID for IP (CIDR range).
    /// </summary>
    private static string CreateIpPatternId(string ip)
    {
        var normalized = NormalizeIpToRange(ip);
        return $"ip:{normalized}";
    }

    /// <summary>
    ///     Simple normalization for fast-path matching.
    ///     Extracts key indicators without expensive regex.
    /// </summary>
    private static string NormalizeForFastPath(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "empty";

        var lower = ua.ToLowerInvariant().Trim();
        var indicators = new List<string>(12); // Pre-sized for "12 basic shapes"

        // Browser detection (mutually exclusive)
        if (lower.Contains("chrome")) indicators.Add("chrome");
        else if (lower.Contains("firefox")) indicators.Add("firefox");
        else if (lower.Contains("safari")) indicators.Add("safari");
        else if (lower.Contains("edge")) indicators.Add("edge");

        // OS detection (mutually exclusive)
        if (lower.Contains("windows")) indicators.Add("windows");
        else if (lower.Contains("mac")) indicators.Add("macos");
        else if (lower.Contains("linux")) indicators.Add("linux");
        else if (lower.Contains("android")) indicators.Add("android");
        else if (lower.Contains("iphone") || lower.Contains("ipad")) indicators.Add("ios");

        // Bot indicators (can be multiple)
        if (lower.Contains("bot")) indicators.Add("bot");
        if (lower.Contains("crawler")) indicators.Add("crawler");
        if (lower.Contains("spider")) indicators.Add("spider");
        if (lower.Contains("scraper")) indicators.Add("scraper");
        if (lower.Contains("headless")) indicators.Add("headless");
        if (lower.Contains("python")) indicators.Add("python");
        if (lower.Contains("curl")) indicators.Add("curl");
        if (lower.Contains("wget")) indicators.Add("wget");

        // Length bucket
        var lengthBucket = ua.Length switch
        {
            < 20 => "tiny",
            < 50 => "short",
            < 150 => "normal",
            < 300 => "long",
            _ => "huge"
        };
        indicators.Add($"len:{lengthBucket}");

        return string.Join(",", indicators.OrderBy(x => x));
    }

    /// <summary>
    ///     Normalize IP to CIDR range for pattern matching.
    /// </summary>
    private static string NormalizeIpToRange(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return "unknown";

        // Handle IPv6
        if (ip.Contains(':'))
        {
            var parts = ip.Split(':');
            if (parts.Length >= 3) return $"{parts[0]}:{parts[1]}:{parts[2]}::/48";
            return ip;
        }

        // Handle IPv4 - normalize to /24
        var octets = ip.Split('.');
        if (octets.Length == 4) return $"{octets[0]}.{octets[1]}.{octets[2]}.0/24";

        return ip;
    }

    /// <summary>
    ///     Compute SHA256 hash of input, return first 16 chars.
    /// </summary>
    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}