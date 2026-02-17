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
public partial class ReputationBiasContributor : ConfiguredContributorBase
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

        // Check User-Agent reputation
        if (!string.IsNullOrWhiteSpace(state.UserAgent))
        {
            var uaPatternId = CreateUaPatternId(state.UserAgent);
            var uaReputation = _reputationCache.Get(uaPatternId);

            if (uaReputation != null && uaReputation.State != ReputationState.Neutral)
            {
                var contribution = CreateReputationContribution(
                    state,
                    uaReputation,
                    "UserAgent",
                    $"UA pattern {uaReputation.State} (score={uaReputation.BotScore:F2}, support={uaReputation.Support:F0})");

                if (contribution != null)
                {
                    contributions.Add(contribution);

                    _logger.LogDebug(
                        "UA reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        uaPatternId, uaReputation.State, uaReputation.BotScore);
                }
            }
        }

        // Check IP reputation - prefer resolved IP from IpContributor
        var resolvedIp = state.Signals.TryGetValue(SignalKeys.ClientIp, out var ipObj)
            ? ipObj?.ToString()
            : state.ClientIp;
        if (!string.IsNullOrWhiteSpace(resolvedIp))
        {
            var ipPatternId = CreateIpPatternId(resolvedIp);
            var ipReputation = _reputationCache.Get(ipPatternId);

            if (ipReputation != null && ipReputation.State != ReputationState.Neutral)
            {
                var contribution = CreateReputationContribution(
                    state,
                    ipReputation,
                    "IP",
                    $"IP range {ipReputation.State} (score={ipReputation.BotScore:F2}, support={ipReputation.Support:F0})");

                if (contribution != null)
                {
                    contributions.Add(contribution);

                    _logger.LogDebug(
                        "IP reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        ipPatternId, ipReputation.State, ipReputation.BotScore);
                }
            }
        }

        // Check combined signature reputation (UA + IP + Path)
        if (!string.IsNullOrWhiteSpace(state.UserAgent) && !string.IsNullOrWhiteSpace(resolvedIp))
        {
            var path = state.HttpContext?.Request?.Path.Value ?? "/";
            var combinedPatternId = CreateCombinedPatternId(state.UserAgent, resolvedIp, path);
            var combinedReputation = _reputationCache.Get(combinedPatternId);

            if (combinedReputation != null && combinedReputation.State != ReputationState.Neutral)
            {
                var contribution = CreateReputationContribution(
                    state,
                    combinedReputation,
                    "Combined",
                    $"Combined signature {combinedReputation.State} (score={combinedReputation.BotScore:F2}, support={combinedReputation.Support:F0})");

                if (contribution != null)
                {
                    // Combined patterns get higher weight as they're more specific
                    contributions.Add(contribution with { Weight = contribution.Weight * CombinedPatternMultiplier });

                    _logger.LogDebug(
                        "Combined reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        combinedPatternId, combinedReputation.State, combinedReputation.BotScore);
                }
            }
        }

        // If we have any reputation-based contributions, add summary signal
        if (contributions.Count > 0)
        {
            state.WriteSignal(SignalKeys.ReputationBiasApplied, true);
            state.WriteSignal(SignalKeys.ReputationBiasCount, contributions.Count);

            // Check if any pattern can trigger fast abort
            var canAbort = state.Signals.TryGetValue(SignalKeys.ReputationCanAbort, out var v) && v is true;

            if (canAbort) state.WriteSignal(SignalKeys.ReputationCanAbort, true);
        }

        // Always return at least one contribution so detector shows in list
        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, "ReputationBias", "No learned reputation patterns matched"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private DetectionContribution? CreateReputationContribution(
            BlackboardState state,
            PatternReputation reputation,
            string category,
            string reason)
    {
        var catLower = category.ToLowerInvariant();
        state.WriteSignals([
            new($"reputation.{catLower}.state", reputation.State.ToString()),
            new($"reputation.{catLower}.score", reputation.BotScore),
            new($"reputation.{catLower}.support", reputation.Support)
        ]);

        // Calculate contribution based on reputation state and FastPathWeight
        var weight = reputation.FastPathWeight;

        // Determine if this should trigger early exit
        if (reputation.CanTriggerFastAbort)
        {
            state.WriteSignal(SignalKeys.ReputationCanAbort, true);

            return DetectionContribution.VerifiedBot(
                    Name,
                    reputation.PatternId,
                    $"[Reputation] {reason}") with
                {
                    ConfidenceDelta = reputation.BotScore,
                    Weight = ConfirmedBadWeight // High weight for confirmed bad patterns
                };
        }

        // For non-abort cases, return weighted contribution
        if (Math.Abs(weight) < 0.01)
            // Negligible weight, skip
            return null;

        string? botType = reputation.State switch
        {
            ReputationState.ConfirmedBad => BotType.MaliciousBot.ToString(),
            ReputationState.Suspect => BotType.Scraper.ToString(),
            ReputationState.ManuallyBlocked => BotType.MaliciousBot.ToString(),
            _ => null
        };

        return new DetectionContribution
        {
            DetectorName = Name,
            Category = $"Reputation:{category}",
            ConfidenceDelta = weight > 0 ? weight : -Math.Abs(weight),
            Weight = Math.Abs(weight) * ReputationWeightMultiplier, // Reputation has decent weight
            Reason = reason,
            BotType = botType
        };
    }

    private static string CreateUaPatternId(string userAgent)
        => PatternNormalization.CreateUaPatternId(userAgent);

    private static string CreateIpPatternId(string ip)
        => PatternNormalization.CreateIpPatternId(ip);

    private static string CreateCombinedPatternId(string userAgent, string ip, string path)
    {
        var uaNorm = PatternNormalization.NormalizeUserAgent(userAgent);
        var ipNorm = PatternNormalization.NormalizeIpToRange(ip);
        var pathNorm = NormalizePath(path);

        var combined = $"{uaNorm}|{ipNorm}|{pathNorm}";
        var hash = PatternNormalization.ComputeHash(combined);
        return $"combined:{hash}";
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
        normalized = GuidRegex().Replace(normalized, "{guid}");

        // Replace numeric IDs
        normalized = NumericIdRegex().Replace(normalized, "/{id}$1");

        return normalized;
    }

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"/\d+(/|$)")]
    private static partial Regex NumericIdRegex();
}