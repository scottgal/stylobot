using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     RankerAtom (per Taxonomy.md) that biases the current request's score
///     with learned patterns from prior detections. UA hash, IP range, and
///     combined UA+IP+path signatures each have accumulated reputation via
///     <see cref="IPatternReputationCache"/> (fed by the reputation
///     maintenance service). Priority 45 -- runs after the sensors have
///     produced UA + IP, before the late heuristic pass.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ReputationBiasContributor</c>.
///     </para>
///     <para>
///         Bias-only: never early-exits, always contributes weighted
///         probability. Coarse patterns (UA / IP / UA+IP+path) let too many
///         real clients share the same key -- fast-abort is
///         <c>FastPathReputationAtom</c>'s territory via PrimarySignature.
///     </para>
///     <para>
///         Browser-attestation carve-out (Sec-Fetch-Site: same-origin)
///         suppresses ConfirmedBad/ManuallyBlocked-driven contributions to
///         zero by default so real Chrome XHRs don't get inflated by a
///         stale UA-hash latch. Operator can restore the old downgrade
///         behaviour via <c>browser_attestation_max_confidence</c>.
///     </para>
/// </remarks>
public sealed partial class ReputationBiasAtom : DetectorAtomBase
{
    private readonly ILogger<ReputationBiasAtom> _logger;
    private readonly IPatternReputationCache _reputationCache;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ReputationBiasAtom(
        ILogger<ReputationBiasAtom> logger,
        IPatternReputationCache reputationCache,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "ReputationBias", category: "ReputationBias")
    {
        _logger = logger;
        _reputationCache = reputationCache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 45;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgent };

    private double ConfirmedBadWeight => _configProvider.GetParameter(Name, "confirmed_bad_weight", 2.5);
    private double CombinedPatternMultiplier => _configProvider.GetParameter(Name, "combined_pattern_multiplier", 1.5);
    private double ReputationWeightMultiplier => _configProvider.GetParameter(Name, "reputation_weight_multiplier", 1.0);
    private double BrowserAttestationMaxConfidence => _configProvider.GetParameter(Name, "browser_attestation_max_confidence", 0.0);
    private double BrowserAttestationWeight => _configProvider.GetParameter(Name, "browser_attestation_weight", 0.7);
    private double PaidTrafficBiasMultiplier => _configProvider.GetParameter(Name, "paid_traffic_bias_multiplier", 1.5);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var contributions = new List<DetectionContribution>();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var resolvedIp = sink.ReadHint(SignalKeys.ClientIp) ?? context.Connection.RemoteIpAddress?.ToString();

        // UA reputation
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var uaPatternId = PatternNormalization.CreateUaPatternId(userAgent);
            var uaReputation = _reputationCache.Get(uaPatternId);
            if (uaReputation is not null && uaReputation.State != ReputationState.Neutral)
            {
                var contribution = CreateReputationContribution(
                    sink, sessionId, context,
                    uaReputation, "UserAgent",
                    $"UA pattern {uaReputation.State} (score={uaReputation.BotScore:F2}, support={uaReputation.Support:F0})");
                if (contribution is not null)
                {
                    contributions.Add(contribution);
                    _logger.LogDebug(
                        "UA reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        uaPatternId, uaReputation.State, uaReputation.BotScore);
                }
            }
        }

        // IP reputation
        if (!string.IsNullOrWhiteSpace(resolvedIp))
        {
            var ipPatternId = PatternNormalization.CreateIpPatternId(resolvedIp);
            var ipReputation = _reputationCache.Get(ipPatternId);
            if (ipReputation is not null && ipReputation.State != ReputationState.Neutral)
            {
                var contribution = CreateReputationContribution(
                    sink, sessionId, context,
                    ipReputation, "IP",
                    $"IP range {ipReputation.State} (score={ipReputation.BotScore:F2}, support={ipReputation.Support:F0})");
                if (contribution is not null)
                {
                    contributions.Add(contribution);
                    _logger.LogDebug(
                        "IP reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        ipPatternId, ipReputation.State, ipReputation.BotScore);
                }
            }
        }

        // Combined UA + IP + Path reputation
        if (!string.IsNullOrWhiteSpace(userAgent) && !string.IsNullOrWhiteSpace(resolvedIp))
        {
            var path = context.Request.Path.Value ?? "/";
            var combinedPatternId = CreateCombinedPatternId(userAgent, resolvedIp, path);
            var combinedReputation = _reputationCache.Get(combinedPatternId);
            if (combinedReputation is not null && combinedReputation.State != ReputationState.Neutral)
            {
                var contribution = CreateReputationContribution(
                    sink, sessionId, context,
                    combinedReputation, "Combined",
                    $"Combined signature {combinedReputation.State} (score={combinedReputation.BotScore:F2}, support={combinedReputation.Support:F0})");
                if (contribution is not null)
                {
                    contributions.Add(contribution with { Weight = contribution.Weight * CombinedPatternMultiplier });
                    _logger.LogDebug(
                        "Combined reputation bias applied: {PatternId} state={State} score={Score:F2}",
                        combinedPatternId, combinedReputation.State, combinedReputation.BotScore);
                }
            }
        }

        if (contributions.Count > 0)
        {
            sink.Raise($"{SignalKeys.ReputationBiasApplied}:true", sessionId);
            sink.Raise($"{SignalKeys.ReputationBiasCount}:{contributions.Count}", sessionId);
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "No learned reputation patterns matched"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private DetectionContribution? CreateReputationContribution(
        SignalSink sink,
        string sessionId,
        HttpContext context,
        PatternReputation reputation,
        string category,
        string reason)
    {
        var catLower = category.ToLowerInvariant();
        sink.Raise($"reputation.{catLower}.state:{reputation.State}", sessionId);
        sink.Raise($"reputation.{catLower}.score:{reputation.BotScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"reputation.{catLower}.support:{reputation.Support.ToString("F0", CultureInfo.InvariantCulture)}", sessionId);

        var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        var hasBrowserAttestation = string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
        var weight = reputation.FastPathWeight;

        if (reputation.CanTriggerFastAbort)
        {
            sink.Raise($"{SignalKeys.ReputationCanAbort}:true", sessionId);

            if (hasBrowserAttestation)
            {
                var residualBias = BrowserAttestationMaxConfidence;
                _logger.LogInformation(
                    "Reputation bias suppressed: {PatternId} ({Category}) has Sec-Fetch-Site: same-origin - browser attestation outweighs latch (residual_bias={ResidualBias:F2})",
                    reputation.PatternId, category, residualBias);

                if (residualBias <= 0.0)
                    return DetectionContribution.Info(
                        Name, $"Reputation:{category}", $"{reason} (suppressed - browser attestation present)");

                return new DetectionContribution
                {
                    DetectorName = Name,
                    Category = $"Reputation:{category}",
                    ConfidenceDelta = Math.Min(reputation.BotScore, residualBias),
                    Weight = BrowserAttestationWeight,
                    Reason = $"{reason} (downgraded - browser attestation present)"
                };
            }

            return new DetectionContribution
            {
                DetectorName = Name,
                Category = $"Reputation:{category}",
                ConfidenceDelta = Math.Min(reputation.BotScore, 0.75),
                Weight = ConfirmedBadWeight,
                Reason = $"[Reputation] {reason}"
            };
        }

        if (Math.Abs(weight) < 0.01) return null;

        var effectiveWeight = Math.Abs(weight) * ReputationWeightMultiplier;
        if (hasBrowserAttestation && weight > 0)
        {
            effectiveWeight = Math.Min(effectiveWeight, BrowserAttestationWeight);
            reason += " (downgraded - browser attestation present)";
        }

        var isPaidTraffic = sink.ReadBoolHint(SignalKeys.ClickFraudIsPaidTraffic);
        if (isPaidTraffic && weight > 0)
            effectiveWeight *= PaidTrafficBiasMultiplier;

        string? botType = (reputation.State, hasBrowserAttestation) switch
        {
            (ReputationState.ConfirmedBad, false)    => BotType.MaliciousBot.ToString(),
            (ReputationState.ManuallyBlocked, false) => BotType.MaliciousBot.ToString(),
            _                                        => null
        };

        return new DetectionContribution
        {
            DetectorName = Name,
            Category = $"Reputation:{category}",
            ConfidenceDelta = weight > 0 ? weight : -Math.Abs(weight),
            Weight = effectiveWeight,
            Reason = reason,
            BotType = botType
        };
    }

    private static string CreateCombinedPatternId(string userAgent, string ip, string path)
    {
        var uaNorm = PatternNormalization.NormalizeUserAgent(userAgent);
        var ipNorm = PatternNormalization.NormalizeIpToRange(ip);
        var pathNorm = NormalizePath(path);
        var combined = $"{uaNorm}|{ipNorm}|{pathNorm}";
        var hash = PatternNormalization.ComputeHash(combined);
        return $"combined:{hash}";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var normalized = path.ToLowerInvariant();
        normalized = GuidRegex().Replace(normalized, "{guid}");
        normalized = NumericIdRegex().Replace(normalized, "/{id}$1");
        return normalized;
    }

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"/\d+(/|$)")]
    private static partial Regex NumericIdRegex();
}
