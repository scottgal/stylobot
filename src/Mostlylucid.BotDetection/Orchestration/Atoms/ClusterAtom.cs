using System.Globalization;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     RankerAtom (per Taxonomy.md) that consults
///     <see cref="BotClusterService"/> for cluster membership,
///     <see cref="SignatureCoordinator"/> for convergence families, and
///     <see cref="CountryReputationTracker"/> for country-level bot rate.
///     Community-affinity fallback increases resolution on borderline
///     non-clustered signatures without over-classifying them.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ClusterContributor</c>. Priority 850 -- runs in the very late
///         wave after every fingerprinting / behavioural atom has emitted
///         its signals.
///     </para>
///     <para>
///         All decision inputs come off sink hints (PrimarySignature, ASN,
///         country code, datacenter flag). Cluster / family metadata replays
///         to the sink as short-string hints; rich cluster / family objects
///         stay on the service layer where they belong.
///     </para>
/// </remarks>
public sealed class ClusterAtom : DetectorAtomBase
{
    private readonly BotClusterService _clusterService;
    private readonly CountryReputationTracker _countryTracker;
    private readonly SignatureCoordinator _signatureCoordinator;
    private readonly ILogger<ClusterAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;

    public ClusterAtom(
        ILogger<ClusterAtom> logger,
        IDetectorConfigProvider configProvider,
        BotClusterService clusterService,
        CountryReputationTracker countryTracker,
        SignatureCoordinator signatureCoordinator)
        : base(name: "ClusterContributor", category: "Cluster")
    {
        _logger = logger;
        _configProvider = configProvider;
        _clusterService = clusterService;
        _countryTracker = countryTracker;
        _signatureCoordinator = signatureCoordinator;
    }

    public override int Priority => 850;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    private double ProductConfidenceDelta => _configProvider.GetParameter(Name, "product_confidence_delta", 0.4);
    private double NetworkConfidenceDelta => _configProvider.GetParameter(Name, "network_confidence_delta", 0.25);
    private double CommunityAffinityDelta => _configProvider.GetParameter(Name, "community_affinity_delta", 0.08);
    private double CountryHighRateThreshold => _configProvider.GetParameter(Name, "country_high_rate_threshold", 0.7);
    private double CountryVeryHighRateThreshold => _configProvider.GetParameter(Name, "country_very_high_rate_threshold", 0.9);
    private double CountryHighDelta => _configProvider.GetParameter(Name, "country_high_delta", 0.1);
    private double CountryVeryHighDelta => _configProvider.GetParameter(Name, "country_very_high_delta", 0.2);
    private double ConvergenceFamilyBoost => _configProvider.GetParameter(Name, "convergence_family_boost", 0.05);
    private double WeightBotSignal => _configProvider.GetDefaults(Name).Weights.BotSignal;
    private double WeightHumanSignal => _configProvider.GetDefaults(Name).Weights.HumanSignal;

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {

        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = sink.ReadHint(SignalKeys.PrimarySignature);
            var inCluster = false;

            if (!string.IsNullOrEmpty(signature))
            {
                var cluster = _clusterService.FindCluster(signature);
                if (cluster is not null)
                {
                    inCluster = true;
                    EmitClusterSignals(sink, sessionId, cluster);
                    EmitSpectralSignals(sink, sessionId, signature);
                    RecordClusterContribution(cluster, contributions);
                }
            }

            // Convergence family membership
            if (!string.IsNullOrEmpty(signature))
            {
                var family = _signatureCoordinator.GetFamily(signature);
                if (family is not null && family.MemberSignatures.Count > 1)
                {
                    sink.Raise($"{SignalKeys.ConvergenceFamilyId}:{family.FamilyId}", sessionId);
                    sink.Raise($"{SignalKeys.ConvergenceFamilySize}:{family.MemberSignatures.Count}", sessionId);
                    sink.Raise($"{SignalKeys.ConvergenceFormationReason}:{family.FormationReason}", sessionId);
                    sink.Raise($"{SignalKeys.ConvergenceMergeConfidence}:{family.MergeConfidence.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

                    var familyBoost = ConvergenceFamilyBoost * Math.Min(family.MemberSignatures.Count, 3);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "ConvergedFamily",
                        ConfidenceDelta = familyBoost,
                        Weight = 1.0,
                        Reason = $"Part of converged family ({family.MemberSignatures.Count} members, {family.FormationReason})",
                        BotType = BotType.Unknown.ToString()
                    });
                }
            }

            // Community affinity fallback
            if (!inCluster)
            {
                var countryCode = sink.ReadHint(SignalKeys.GeoCountryCode);
                var asn = sink.ReadHint(SignalKeys.IpAsn);
                var isDatacenter = sink.ReadBoolHint(SignalKeys.IpIsDatacenter);

                if (!string.IsNullOrEmpty(asn) || !string.IsNullOrEmpty(countryCode))
                {
                    var bestAffinity = FindCommunityAffinity(asn, countryCode, isDatacenter);
                    if (bestAffinity.HasValue)
                    {
                        var (affinityCluster, affinityScore) = bestAffinity.Value;
                        sink.Raise($"cluster.community_affinity:{Math.Round(affinityScore, 4).ToString(CultureInfo.InvariantCulture)}", sessionId);
                        sink.Raise($"cluster.community_cluster_id:{affinityCluster.ClusterId}", sessionId);

                        var delta = CommunityAffinityDelta * affinityScore;
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "CommunityAffinity",
                            ConfidenceDelta = delta,
                            Weight = 1.0,
                            Reason = $"Shares infrastructure with bot cluster '{affinityCluster.Label}' "
                                     + $"(affinity={affinityScore:F2}, ASN match={!string.IsNullOrEmpty(asn) && string.Equals(asn, affinityCluster.DominantAsn, StringComparison.OrdinalIgnoreCase)})",
                            BotType = BotType.Unknown.ToString()
                        });
                    }
                }
            }

            // Country reputation
            var country = sink.ReadHint(SignalKeys.GeoCountryCode);
            if (!string.IsNullOrEmpty(country))
            {
                var botRate = _countryTracker.GetCountryBotRate(country);
                if (botRate > 0)
                {
                    sink.Raise($"{SignalKeys.GeoCountryBotRate}:{botRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

                    var allCountries = _countryTracker.GetTopBotCountries(100);
                    var rank = 0;
                    for (var i = 0; i < allCountries.Count; i++)
                    {
                        if (string.Equals(allCountries[i].CountryCode, country, StringComparison.OrdinalIgnoreCase))
                        {
                            rank = i + 1;
                            break;
                        }
                    }
                    if (rank > 0)
                        sink.Raise($"{SignalKeys.GeoCountryBotRank}:{rank}", sessionId);

                    if (botRate >= CountryVeryHighRateThreshold)
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "CountryReputation",
                            ConfidenceDelta = CountryVeryHighDelta,
                            Weight = 1.0,
                            Reason = $"Country {country} has very high bot rate ({botRate:F2}, rank #{rank})",
                            BotType = BotType.Unknown.ToString()
                        });
                    }
                    else if (botRate >= CountryHighRateThreshold)
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = "CountryReputation",
                            ConfidenceDelta = CountryHighDelta,
                            Weight = 1.0,
                            Reason = $"Country {country} has elevated bot rate ({botRate:F2}, rank #{rank})",
                            BotType = BotType.Unknown.ToString()
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in cluster/country reputation analysis");
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static void EmitClusterSignals(SignalSink sink, string sessionId, BotCluster cluster)
    {
        sink.Raise($"{SignalKeys.ClusterType}:{cluster.Type.ToString().ToLowerInvariant()}", sessionId);
        sink.Raise($"{SignalKeys.ClusterId}:{cluster.ClusterId}", sessionId);
        sink.Raise($"{SignalKeys.ClusterMemberCount}:{cluster.MemberCount}", sessionId);
        sink.Raise($"{SignalKeys.ClusterAvgBotProbability}:{cluster.AverageBotProbability.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ClusterAvgSimilarity}:{cluster.AverageSimilarity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ClusterTemporalDensity}:{cluster.TemporalDensity.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        if (!string.IsNullOrEmpty(cluster.Label))
            sink.Raise($"{SignalKeys.ClusterLabel}:{cluster.Label}", sessionId);
    }

    private void EmitSpectralSignals(SignalSink sink, string sessionId, string signature)
    {
        var spectral = _clusterService.GetSpectralFeatures(signature);
        if (spectral?.HasSufficientData != true) return;

        sink.Raise($"{SignalKeys.ClusterSpectralEntropy}:{spectral.SpectralEntropy.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ClusterDominantFrequency}:{spectral.DominantFrequency.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ClusterHarmonicRatio}:{spectral.HarmonicRatio.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ClusterPeakToAvg}:{spectral.PeakToAvgRatio.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
    }

    private void RecordClusterContribution(BotCluster cluster, List<DetectionContribution> contributions)
    {
        switch (cluster.Type)
        {
            case BotClusterType.BotProduct:
            {
                var delta = ProductConfidenceDelta * Math.Min(1.0, cluster.AverageBotProbability + 0.2);
                var reason = $"Part of bot product cluster '{cluster.Label}' ({cluster.MemberCount} members, similarity={cluster.AverageSimilarity:F2})";
                if (!string.IsNullOrEmpty(cluster.Description)) reason += $". {cluster.Description}";
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Cluster",
                    ConfidenceDelta = delta,
                    Weight = WeightBotSignal,
                    Reason = reason,
                    BotType = "Scraper"
                });
                break;
            }
            case BotClusterType.BotNetwork:
            {
                var delta = NetworkConfidenceDelta * Math.Min(1.0, cluster.TemporalDensity + 0.2);
                var reason = $"Part of bot network '{cluster.Label}' ({cluster.MemberCount} members, density={cluster.TemporalDensity:F2})";
                if (!string.IsNullOrEmpty(cluster.Description)) reason += $". {cluster.Description}";
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Cluster",
                    ConfidenceDelta = delta,
                    Weight = WeightBotSignal,
                    Reason = reason,
                    BotType = "MaliciousBot"
                });
                break;
            }
            case BotClusterType.Emergent:
            {
                var delta = NetworkConfidenceDelta * 0.5 * Math.Min(1.0, cluster.AverageBotProbability);
                var reason = $"Part of emerging bot cluster '{cluster.Label}' ({cluster.MemberCount} members, similarity={cluster.AverageSimilarity:F2})";
                if (!string.IsNullOrEmpty(cluster.Description)) reason += $". {cluster.Description}";
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Cluster",
                    ConfidenceDelta = delta,
                    Weight = WeightBotSignal * 0.7,
                    Reason = reason,
                    BotType = "Scraper"
                });
                break;
            }
            case BotClusterType.Safe:
            {
                var reason = $"Part of verified-friendly cluster '{cluster.Label}' ({cluster.MemberCount} members)";
                if (!string.IsNullOrEmpty(cluster.Description)) reason += $". {cluster.Description}";
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Cluster",
                    ConfidenceDelta = -0.2,
                    Weight = WeightHumanSignal,
                    Reason = reason
                });
                break;
            }
            case BotClusterType.HumanTraffic:
            {
                var reason = $"Part of human-traffic cluster '{cluster.Label}' ({cluster.MemberCount} members, similarity={cluster.AverageSimilarity:F2})";
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Cluster",
                    ConfidenceDelta = -0.15,
                    Weight = WeightHumanSignal * 0.7,
                    Reason = reason
                });
                break;
            }
            case BotClusterType.Mixed:
            case BotClusterType.Unknown:
            default:
                break;
        }
    }

    private (BotCluster Cluster, double Score)? FindCommunityAffinity(
        string? asn, string? countryCode, bool isDatacenter)
    {
        var clusters = _clusterService.GetClusters();
        if (clusters.Count == 0) return null;

        BotCluster? bestCluster = null;
        var bestScore = 0.0;

        foreach (var cluster in clusters)
        {
            var score = 0.0;
            var factors = 0;

            if (!string.IsNullOrEmpty(asn) && !string.IsNullOrEmpty(cluster.DominantAsn))
            {
                factors++;
                if (string.Equals(asn, cluster.DominantAsn, StringComparison.OrdinalIgnoreCase))
                    score += 0.5;
            }

            if (!string.IsNullOrEmpty(countryCode) && !string.IsNullOrEmpty(cluster.DominantCountry))
            {
                factors++;
                if (string.Equals(countryCode, cluster.DominantCountry, StringComparison.OrdinalIgnoreCase))
                    score += 0.3;
            }

            if (isDatacenter)
            {
                factors++;
                score += 0.2;
            }

            if (factors == 0 || score < 0.3) continue;

            score *= Math.Min(1.0, cluster.AverageBotProbability);

            if (score > bestScore)
            {
                bestScore = score;
                bestCluster = cluster;
            }
        }

        return bestCluster is not null && bestScore >= 0.2
            ? (bestCluster, Math.Min(1.0, bestScore))
            : null;
    }
}
