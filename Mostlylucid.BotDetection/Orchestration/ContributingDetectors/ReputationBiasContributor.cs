using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Early-stage contributor that queries the PatternReputationCache to provide
///     initial bias based on learned patterns from prior detections.
///     This closes the learning feedback loop by ensuring that:
///     1. Patterns learned from prior requests (via ReputationMaintenanceService)
///     2. Are fed back into early detection for similar future requests
///     Pattern types checked:
///     - User-Agent hash (normalized)
///     - IP range (/24 for IPv4, /48 for IPv6)
///     - Combined signature (UA + IP + Path)
///     Runs in Wave 0 (first wave) with high priority to provide bias before
///     other detectors run their analysis.
///
///     Configuration loaded from: reputation.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:ReputationBiasContributor:*
/// </summary>
public class ReputationBiasContributor : ConfiguredContributorBase
{
    private readonly ILogger<ReputationBiasContributor> _logger;
    private readonly IPatternReputationCache _reputationCache;

    public ReputationBiasContributor(
        ILogger<ReputationBiasContributor> logger,
        IPatternReputationCache reputationCache,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _reputationCache = reputationCache;
    }

    public override string Name => "ReputationBias";

    public override int Priority => Manifest?.Priority ?? 45;

    // Config-driven parameters from YAML
    private double ConfirmedBadWeight => GetParam("confirmed_bad_weight", 2.5);
    private double CombinedPatternMultiplier => GetParam("combined_pattern_multiplier", 1.5);
    private double ReputationWeightMultiplier => GetParam("reputation_weight_multiplier", 1.5);

    // Trigger when we have the basic signals extracted
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.UserAgent) // Wait until UA is extracted
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary<string, object>.Empty;

        // Check User-Agent reputation
        if (!string.IsNullOrWhiteSpace(state.UserAgent))
        {
            var uaPatternId = CreateUaPatternId(state.UserAgent);
            var uaReputation = _reputationCache.Get(uaPatternId);

            if (uaReputation != null && uaReputation.State != ReputationState.Neutral)
            {
                var (contribution, uaSignals) = CreateReputationContribution(
                    uaReputation,
                    "UserAgent",
                    $"UA pattern {uaReputation.State} (score={uaReputation.BotScore:F2}, support={uaReputation.Support:F0})");

                if (contribution != null)
                {
                    contributions.Add(contribution);
                    signals = signals.AddRange(uaSignals);

                    _logger.LogDebug(
                        "UA reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        uaPatternId, uaReputation.State, uaReputation.BotScore);
                }
            }
        }

        // Check IP reputation
        if (!string.IsNullOrWhiteSpace(state.ClientIp))
        {
            var ipPatternId = CreateIpPatternId(state.ClientIp);
            var ipReputation = _reputationCache.Get(ipPatternId);

            if (ipReputation != null && ipReputation.State != ReputationState.Neutral)
            {
                var (contribution, ipSignals) = CreateReputationContribution(
                    ipReputation,
                    "IP",
                    $"IP range {ipReputation.State} (score={ipReputation.BotScore:F2}, support={ipReputation.Support:F0})");

                if (contribution != null)
                {
                    contributions.Add(contribution);
                    signals = signals.AddRange(ipSignals);

                    _logger.LogDebug(
                        "IP reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        ipPatternId, ipReputation.State, ipReputation.BotScore);
                }
            }
        }

        // Check combined signature reputation (UA + IP + Path)
        if (!string.IsNullOrWhiteSpace(state.UserAgent) && !string.IsNullOrWhiteSpace(state.ClientIp))
        {
            var path = state.HttpContext?.Request?.Path.Value ?? "/";
            var combinedPatternId = CreateCombinedPatternId(state.UserAgent, state.ClientIp, path);
            var combinedReputation = _reputationCache.Get(combinedPatternId);

            if (combinedReputation != null && combinedReputation.State != ReputationState.Neutral)
            {
                var (contribution, combinedSignals) = CreateReputationContribution(
                    combinedReputation,
                    "Combined",
                    $"Combined signature {combinedReputation.State} (score={combinedReputation.BotScore:F2}, support={combinedReputation.Support:F0})");

                if (contribution != null)
                {
                    // Combined patterns get higher weight as they're more specific
                    contributions.Add(contribution with { Weight = contribution.Weight * CombinedPatternMultiplier });
                    signals = signals.AddRange(combinedSignals);

                    _logger.LogDebug(
                        "Combined reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        combinedPatternId, combinedReputation.State, combinedReputation.BotScore);
                }
            }
        }

        // If we have any reputation-based contributions, add summary signal
        if (contributions.Count > 0)
        {
            signals = signals.Add(SignalKeys.ReputationBiasApplied, true);
            signals = signals.Add(SignalKeys.ReputationBiasCount, contributions.Count);

            // Check if any pattern can trigger fast abort
            var canAbort = contributions.Any(c =>
                c.Signals.TryGetValue(SignalKeys.ReputationCanAbort, out var v) && v is true);

            if (canAbort) signals = signals.Add(SignalKeys.ReputationCanAbort, true);

            // Update signals on first contribution to contain summary
            if (contributions.Count > 0)
            {
                var existingSignals = contributions[0].Signals;
                var merged = existingSignals.ToImmutableDictionary().SetItems(signals);
                contributions[0] = contributions[0] with { Signals = merged };
            }
        }

        // Always return at least one contribution so detector shows in list
        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, "ReputationBias", "No learned reputation patterns matched"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private (DetectionContribution? Contribution, ImmutableDictionary<string, object> Signals)
        CreateReputationContribution(
            PatternReputation reputation,
            string category,
            string reason)
    {
        var signals = ImmutableDictionary<string, object>.Empty
            .Add($"reputation.{category.ToLowerInvariant()}.state", reputation.State.ToString())
            .Add($"reputation.{category.ToLowerInvariant()}.score", reputation.BotScore)
            .Add($"reputation.{category.ToLowerInvariant()}.support", reputation.Support);

        // Calculate contribution based on reputation state and FastPathWeight
        var weight = reputation.FastPathWeight;

        // Determine if this should trigger early exit
        if (reputation.CanTriggerFastAbort)
        {
            signals = signals.Add(SignalKeys.ReputationCanAbort, true);

            return (DetectionContribution.VerifiedBot(
                    Name,
                    reputation.PatternId,
                    $"[Reputation] {reason}") with
                {
                    ConfidenceDelta = reputation.BotScore,
                    Weight = ConfirmedBadWeight, // High weight for confirmed bad patterns
                    Signals = signals
                }, signals);
        }

        // For non-abort cases, return weighted contribution
        if (Math.Abs(weight) < 0.01)
            // Negligible weight, skip
            return (null, ImmutableDictionary<string, object>.Empty);

        string? botType = reputation.State switch
        {
            ReputationState.ConfirmedBad => BotType.MaliciousBot.ToString(),
            ReputationState.Suspect => BotType.Scraper.ToString(),
            ReputationState.ManuallyBlocked => BotType.MaliciousBot.ToString(),
            _ => null
        };

        return (new DetectionContribution
        {
            DetectorName = Name,
            Category = $"Reputation:{category}",
            ConfidenceDelta = weight > 0 ? weight : -Math.Abs(weight),
            Weight = Math.Abs(weight) * ReputationWeightMultiplier, // Reputation has decent weight
            Reason = $"[Reputation] {reason}",
            BotType = botType,
            Signals = signals
        }, signals);
    }

    /// <summary>
    ///     Create pattern ID for User-Agent (normalized hash).
    ///     Uses same normalization as SignatureFeedbackHandler for consistency.
    /// </summary>
    private static string CreateUaPatternId(string userAgent)
    {
        // Normalize: lowercase, trim, remove version numbers for broader matching
        var normalized = NormalizeUserAgent(userAgent);
        var hash = ComputeHash(normalized);
        return $"ua:{hash}";
    }

    /// <summary>
    ///     Create pattern ID for IP (CIDR range).
    ///     Uses /24 for IPv4, /48 for IPv6 - same as SignatureFeedbackHandler.
    /// </summary>
    private static string CreateIpPatternId(string ip)
    {
        var normalized = NormalizeIpToRange(ip);
        return $"ip:{normalized}";
    }

    /// <summary>
    ///     Create pattern ID for combined signature (UA + IP + Path).
    /// </summary>
    private static string CreateCombinedPatternId(string userAgent, string ip, string path)
    {
        var uaNorm = NormalizeUserAgent(userAgent);
        var ipNorm = NormalizeIpToRange(ip);
        var pathNorm = NormalizePath(path);

        var combined = $"{uaNorm}|{ipNorm}|{pathNorm}";
        var hash = ComputeHash(combined);
        return $"combined:{hash}";
    }

    /// <summary>
    ///     Normalize User-Agent for pattern matching.
    ///     Removes version numbers, normalizes casing, extracts key indicators.
    /// </summary>
    private static string NormalizeUserAgent(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "empty";

        var lower = ua.ToLowerInvariant().Trim();

        // Extract key indicators
        var indicators = new List<string>();

        // Browser detection
        if (lower.Contains("chrome")) indicators.Add("chrome");
        else if (lower.Contains("firefox")) indicators.Add("firefox");
        else if (lower.Contains("safari") && !lower.Contains("chrome")) indicators.Add("safari");
        else if (lower.Contains("edge")) indicators.Add("edge");

        // OS detection
        if (lower.Contains("windows")) indicators.Add("windows");
        else if (lower.Contains("mac os") || lower.Contains("macintosh")) indicators.Add("macos");
        else if (lower.Contains("linux")) indicators.Add("linux");
        else if (lower.Contains("android")) indicators.Add("android");
        else if (lower.Contains("iphone") || lower.Contains("ipad")) indicators.Add("ios");

        // Bot indicators
        if (lower.Contains("bot")) indicators.Add("bot");
        if (lower.Contains("crawler")) indicators.Add("crawler");
        if (lower.Contains("spider")) indicators.Add("spider");
        if (lower.Contains("scraper")) indicators.Add("scraper");
        if (lower.Contains("headless")) indicators.Add("headless");
        if (lower.Contains("python")) indicators.Add("python");
        if (lower.Contains("curl")) indicators.Add("curl");
        if (lower.Contains("wget")) indicators.Add("wget");

        // Length bucket (for anomaly detection)
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
            // Simplify: just take first 3 groups for /48
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
    ///     Normalize path for pattern matching.
    ///     Replaces IDs/GUIDs with placeholders.
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.ToLowerInvariant();

        // Replace GUIDs
        normalized = Regex.Replace(
            normalized,
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            "{guid}");

        // Replace numeric IDs
        normalized = Regex.Replace(
            normalized,
            @"/\d+(/|$)",
            "/{id}$1");

        return normalized;
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