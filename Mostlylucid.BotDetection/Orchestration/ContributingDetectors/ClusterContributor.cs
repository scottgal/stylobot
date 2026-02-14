using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Bot cluster detection and country reputation contributor.
///     Checks if the current request's signature belongs to a discovered cluster
///     (same bot software or coordinated campaign) and applies country-level
///     bot rate reputation as a detection signal.
///
///     For marginal detections (not yet in a cluster), checks "community affinity":
///     if the request shares ASN/country with a known bot cluster, applies a small
///     resolution-increasing boost to help distinguish borderline cases.
///
///     Runs in Wave 2 (after GeoContributor and BehavioralWaveform).
///
///     Configuration loaded from: cluster.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:ClusterContributor:*
/// </summary>
public class ClusterContributor : ConfiguredContributorBase
{
    private readonly BotClusterService _clusterService;
    private readonly CountryReputationTracker _countryTracker;
    private readonly SignatureCoordinator _signatureCoordinator;
    private readonly ILogger<ClusterContributor> _logger;

    public ClusterContributor(
        ILogger<ClusterContributor> logger,
        IDetectorConfigProvider configProvider,
        BotClusterService clusterService,
        CountryReputationTracker countryTracker,
        SignatureCoordinator signatureCoordinator)
        : base(configProvider)
    {
        _logger = logger;
        _clusterService = clusterService;
        _countryTracker = countryTracker;
        _signatureCoordinator = signatureCoordinator;
    }

    public override string Name => "ClusterContributor";
    public override int Priority => Manifest?.Priority ?? 850;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.WaveformSignature)
    ];

    // Config-driven parameters (no magic numbers)
    private double ProductConfidenceDelta => GetParam("product_confidence_delta", 0.4);
    private double NetworkConfidenceDelta => GetParam("network_confidence_delta", 0.25);
    private double CommunityAffinityDelta => GetParam("community_affinity_delta", 0.08);
    private double CountryHighRateThreshold => GetParam("country_high_rate_threshold", 0.7);
    private double CountryVeryHighRateThreshold => GetParam("country_very_high_rate_threshold", 0.9);
    private double CountryHighDelta => GetParam("country_high_delta", 0.1);
    private double CountryVeryHighDelta => GetParam("country_very_high_delta", 0.2);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            // 1. Check cluster membership
            var signature = state.GetSignal<string>(SignalKeys.WaveformSignature);
            var inCluster = false;

            if (!string.IsNullOrEmpty(signature))
            {
                var cluster = _clusterService.FindCluster(signature);
                if (cluster != null)
                {
                    inCluster = true;
                    signals[SignalKeys.ClusterType] = cluster.Type.ToString().ToLowerInvariant();
                    signals[SignalKeys.ClusterId] = cluster.ClusterId;
                    signals[SignalKeys.ClusterMemberCount] = cluster.MemberCount;
                    signals[SignalKeys.ClusterAvgBotProbability] = cluster.AverageBotProbability;
                    signals[SignalKeys.ClusterAvgSimilarity] = cluster.AverageSimilarity;
                    signals[SignalKeys.ClusterTemporalDensity] = cluster.TemporalDensity;

                    if (!string.IsNullOrEmpty(cluster.Label))
                        signals[SignalKeys.ClusterLabel] = cluster.Label;

                    // Emit spectral features if available
                    var spectral = _clusterService.GetSpectralFeatures(signature);
                    if (spectral?.HasSufficientData == true)
                    {
                        signals[SignalKeys.ClusterSpectralEntropy] = spectral.SpectralEntropy;
                        signals[SignalKeys.ClusterDominantFrequency] = spectral.DominantFrequency;
                        signals[SignalKeys.ClusterHarmonicRatio] = spectral.HarmonicRatio;
                        signals[SignalKeys.ClusterPeakToAvg] = spectral.PeakToAvgRatio;
                    }

                    switch (cluster.Type)
                    {
                        case BotClusterType.BotProduct:
                        {
                            // High confidence boost: same bot software across different IPs
                            var delta = ProductConfidenceDelta *
                                        Math.Min(1.0, cluster.AverageBotProbability + 0.2);
                            contributions.Add(BotContribution(
                                "Cluster",
                                $"Part of bot product cluster '{cluster.Label}' ({cluster.MemberCount} members, similarity={cluster.AverageSimilarity:F2})",
                                confidenceOverride: delta,
                                weightMultiplier: WeightBotSignal,
                                botType: "Scraper"));
                            break;
                        }
                        case BotClusterType.BotNetwork:
                        {
                            // Moderate confidence boost: coordinated campaign
                            var delta = NetworkConfidenceDelta *
                                        Math.Min(1.0, cluster.TemporalDensity + 0.2);
                            contributions.Add(BotContribution(
                                "Cluster",
                                $"Part of bot network '{cluster.Label}' ({cluster.MemberCount} members, density={cluster.TemporalDensity:F2})",
                                confidenceOverride: delta,
                                weightMultiplier: WeightBotSignal,
                                botType: "MaliciousBot"));
                            break;
                        }
                    }
                }
            }

            // 1b. Check convergence family membership
            if (!string.IsNullOrEmpty(signature))
            {
                var family = _signatureCoordinator.GetFamily(signature);
                if (family != null && family.MemberSignatures.Count > 1)
                {
                    signals[SignalKeys.ConvergenceFamilyId] = family.FamilyId;
                    signals[SignalKeys.ConvergenceFamilySize] = family.MemberSignatures.Count;
                    signals[SignalKeys.ConvergenceFormationReason] = family.FormationReason.ToString();
                    signals[SignalKeys.ConvergenceMergeConfidence] = family.MergeConfidence;

                    // Family-level boost: more members = stronger signal, capped at 3
                    var familyBoost = GetParam("convergence_family_boost", 0.05) *
                                     Math.Min(family.MemberSignatures.Count, 3);
                    contributions.Add(BotContribution(
                        "ConvergedFamily",
                        $"Part of converged family ({family.MemberSignatures.Count} members, {family.FormationReason})",
                        confidenceOverride: familyBoost));
                }
            }

            // 2. Community affinity: marginal signatures that share infrastructure with known bot clusters
            //    This INCREASES RESOLUTION on borderline calls without making arbitrary associations
            if (!inCluster)
            {
                var countryCode = state.GetSignal<string>("geo.country_code");
                var asn = state.GetSignal<string>("request.ip.asn");
                var isDatacenter = state.GetSignal<bool>("request.ip.is_datacenter");

                if (!string.IsNullOrEmpty(asn) || !string.IsNullOrEmpty(countryCode))
                {
                    var bestAffinity = FindCommunityAffinity(asn, countryCode, isDatacenter);
                    if (bestAffinity.HasValue)
                    {
                        var (affinityCluster, affinityScore) = bestAffinity.Value;
                        signals["cluster.community_affinity"] = Math.Round(affinityScore, 4);
                        signals["cluster.community_cluster_id"] = affinityCluster.ClusterId;

                        // Small proportional boost: community features increase resolution
                        // but never on their own make something a bot
                        var delta = CommunityAffinityDelta * affinityScore;
                        contributions.Add(BotContribution(
                            "CommunityAffinity",
                            $"Shares infrastructure with bot cluster '{affinityCluster.Label}' " +
                            $"(affinity={affinityScore:F2}, ASN match={!string.IsNullOrEmpty(asn) && string.Equals(asn, affinityCluster.DominantAsn, StringComparison.OrdinalIgnoreCase)})",
                            confidenceOverride: delta));
                    }
                }
            }

            // 3. Check country reputation
            {
                var countryCode = state.GetSignal<string>("geo.country_code");
                if (!string.IsNullOrEmpty(countryCode))
                {
                    var botRate = _countryTracker.GetCountryBotRate(countryCode);
                    if (botRate > 0)
                    {
                        signals[SignalKeys.GeoCountryBotRate] = botRate;

                        // Get rank among all tracked countries
                        var allCountries = _countryTracker.GetTopBotCountries(100);
                        var rank = 0;
                        for (var i = 0; i < allCountries.Count; i++)
                        {
                            if (string.Equals(allCountries[i].CountryCode, countryCode,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                rank = i + 1;
                                break;
                            }
                        }

                        if (rank > 0)
                            signals[SignalKeys.GeoCountryBotRank] = rank;

                        if (botRate >= CountryVeryHighRateThreshold)
                        {
                            contributions.Add(BotContribution(
                                "CountryReputation",
                                $"Country {countryCode} has very high bot rate ({botRate:F2}, rank #{rank})",
                                confidenceOverride: CountryVeryHighDelta));
                        }
                        else if (botRate >= CountryHighRateThreshold)
                        {
                            contributions.Add(BotContribution(
                                "CountryReputation",
                                $"Country {countryCode} has elevated bot rate ({botRate:F2}, rank #{rank})",
                                confidenceOverride: CountryHighDelta));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in cluster/country reputation analysis");
        }

        // Ensure signals are attached to the last contribution
        if (contributions.Count == 0)
        {
            if (signals.Count > 0)
            {
                contributions.Add(NeutralContribution("Cluster",
                    "Cluster and country reputation analysis complete") with
                {
                    Signals = signals.ToImmutable()
                });
            }
        }
        else
        {
            var last = contributions[^1];
            contributions[^1] = last with { Signals = signals.ToImmutable() };
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Find the best "community affinity" match: checks if a non-clustered signature
    ///     shares infrastructure (ASN, country, datacenter) with a known bot cluster.
    ///     Returns a score [0,1] where 1.0 = perfect match.
    /// </summary>
    private (BotCluster Cluster, double Score)? FindCommunityAffinity(
        string? asn, string? countryCode, bool isDatacenter)
    {
        var clusters = _clusterService.GetClusters();
        if (clusters.Count == 0)
            return null;

        BotCluster? bestCluster = null;
        var bestScore = 0.0;

        foreach (var cluster in clusters)
        {
            var score = 0.0;
            var factors = 0;

            // ASN match is strongest signal (same hosting provider / ISP)
            if (!string.IsNullOrEmpty(asn) && !string.IsNullOrEmpty(cluster.DominantAsn))
            {
                factors++;
                if (string.Equals(asn, cluster.DominantAsn, StringComparison.OrdinalIgnoreCase))
                    score += 0.5;
            }

            // Country match
            if (!string.IsNullOrEmpty(countryCode) && !string.IsNullOrEmpty(cluster.DominantCountry))
            {
                factors++;
                if (string.Equals(countryCode, cluster.DominantCountry, StringComparison.OrdinalIgnoreCase))
                    score += 0.3;
            }

            // Datacenter presence (bots tend to run in datacenters)
            if (isDatacenter)
            {
                factors++;
                score += 0.2;
            }

            // Require at least ASN or country match to avoid noise
            if (factors == 0 || score < 0.3)
                continue;

            // Weight by cluster confidence (high avg bot probability = more trustworthy cluster)
            score *= Math.Min(1.0, cluster.AverageBotProbability);

            if (score > bestScore)
            {
                bestScore = score;
                bestCluster = cluster;
            }
        }

        // Only return if affinity is meaningful (at least ASN or country match)
        if (bestCluster != null && bestScore >= 0.2)
            return (bestCluster, Math.Min(1.0, bestScore));

        return null;
    }
}
