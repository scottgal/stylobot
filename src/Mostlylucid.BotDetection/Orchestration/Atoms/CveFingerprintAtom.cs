using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ProposerAtom (per Taxonomy.md) that matches the current session's
///     radar shape against CVE-derived fingerprints. Runs at Priority 55 —
///     between HeuristicAtom (50) and SimilarityAtom (60). Native
///     <see cref="IDetectorAtom"/> replacement for
///     <c>CveFingerprintContributor</c>.
///     When a match is found, raises signals with CVE correlation metadata
///     and contributes a bot detection signal proportional to match
///     confidence and advisory severity.
/// </summary>
public sealed class CveFingerprintAtom : DetectorAtomBase
{
    private readonly ICveFingerprintMatcher _matcher;
    private readonly ILogger<CveFingerprintAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;

    public CveFingerprintAtom(
        ICveFingerprintMatcher matcher,
        ILogger<CveFingerprintAtom> logger,
        IDetectorConfigProvider configProvider)
        : base(name: "CveFingerprint", category: "CveFingerprint")
    {
        _matcher = matcher;
        _logger = logger;
        _configProvider = configProvider;
    }

    public override int Priority => 55;

    /// <summary>
    ///     Requires heuristic prediction to have run so radar dimensions can
    ///     be built from feature signals.
    /// </summary>
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.HeuristicPrediction };

    private double MatchThreshold => _configProvider.GetParameter(Name, "match_threshold", 0.80);
    private double CveWeight => _configProvider.GetParameter(Name, "cve_weight", 1.5);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        if (_matcher.FingerprintCount == 0)
        {
            sink.Raise($"{SignalKeys.CveMatchCount}:0", sessionId);
            return None();
        }

        try
        {
            var dimensions = BuildRadarDimensionsFromSink(sink);

            var matches = await _matcher.FindMatchesAsync(
                dimensions, topK: 5, minSimilarity: MatchThreshold, ct).ConfigureAwait(false);

            sink.Raise($"{SignalKeys.CveMatchCount}:{matches.Count}", sessionId);

            if (matches.Count == 0) return None();

            var topMatch = matches[0];
            sink.Raise($"{SignalKeys.CveTopAdvisoryId}:{topMatch.AdvisoryId}", sessionId);
            sink.Raise($"{SignalKeys.CveTopSimilarity}:{topMatch.Similarity:F4}", sessionId);
            sink.Raise($"{SignalKeys.CveTopSeverity}:{topMatch.Severity}", sessionId);

            if (topMatch.ClusterLabel is not null)
                sink.Raise($"{SignalKeys.CveClusterLabel}:{topMatch.ClusterLabel}", sessionId);

            sink.Raise($"{SignalKeys.CveMatchedIds}:{string.Join(",", matches.Select(m => m.AdvisoryId))}", sessionId);

            var severityBoost = topMatch.Severity switch
            {
                "critical" => _configProvider.GetParameter(Name, "severity_boost_critical", 0.35),
                "high" => _configProvider.GetParameter(Name, "severity_boost_high", 0.25),
                "medium" => _configProvider.GetParameter(Name, "severity_boost_medium", 0.15),
                "low" => _configProvider.GetParameter(Name, "severity_boost_low", 0.08),
                _ => _configProvider.GetParameter(Name, "severity_boost_default", 0.10)
            };
            var confidence = severityBoost * topMatch.Similarity;

            _logger.LogInformation(
                "CVE fingerprint match: {AdvisoryId} ({Severity}) similarity={Similarity:F2} boost={Boost:F3}",
                topMatch.AdvisoryId, topMatch.Severity, topMatch.Similarity, confidence);

            var reason = topMatch.ClusterLabel is not null
                ? $"Traffic matches {topMatch.AdvisoryId} ({topMatch.Severity}) exploit family '{topMatch.ClusterLabel}' ({topMatch.Similarity:P0} match, {matches.Count} total CVE matches)"
                : $"Traffic matches {topMatch.AdvisoryId} ({topMatch.Severity}) CVE fingerprint ({topMatch.Similarity:P0} match, {matches.Count} total CVE matches)";

            return Single(DetectionContribution.Bot(
                Name,
                "CveFingerprint",
                confidence,
                reason,
                weight: CveWeight,
                botType: BotType.ExploitScanner.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CVE fingerprint matching failed");
            sink.Raise($"cve.error:{ex.Message}", sessionId);
            return None();
        }
    }

    /// <summary>
    ///     Build the 16-dimension radar shape from current sink signals.
    ///     Uses hint readers so numeric / boolean signals raised in
    ///     <c>"key:value"</c> form are decoded consistently across the pack.
    /// </summary>
    private static Dictionary<string, double> BuildRadarDimensionsFromSink(SignalSink sink)
    {
        var dims = RadarDimensions.CreateEmpty();

        dims[RadarDimensions.UaAnomaly] = sink.ReadBoolHint(SignalKeys.UserAgentIsBot) ? 0.8 : 0.0;
        dims[RadarDimensions.HeaderAnomaly] =
            sink.ReadBoolHint(SignalKeys.HeadersSuspicious) || sink.ReadBoolHint(SignalKeys.HeadersMissing) ? 0.6 : 0.0;
        dims[RadarDimensions.IpReputation] = sink.ReadBoolHint(SignalKeys.IpIsDatacenter) ? 0.5 : 0.0;
        dims[RadarDimensions.Behavioral] = sink.ReadBoolHint(SignalKeys.BehavioralAnomalyDetected) ? 0.7 : 0.0;
        dims[RadarDimensions.AdvancedBehavioral] = Math.Min(1.0, Math.Max(
            sink.ReadBoolHint(SignalKeys.StreamHandshakeStorm) ? 0.9 : 0.0,
            Math.Max(
                sink.ReadBoolHint(SignalKeys.StreamCrossEndpointMixing) ? 0.8 : 0.0,
                Math.Max(
                    sink.ReadDoubleHint(SignalKeys.WaveformTimingRegularity),
                    Math.Max(
                        sink.ReadDoubleHint(SignalKeys.SessionVelocityMagnitude),
                        sink.ReadBoolHint(SignalKeys.WaveformBurstDetected) ? 0.75 : 0.0)))));
        dims[RadarDimensions.CacheBehavior] = sink.ReadBoolHint(SignalKeys.CacheBehaviorAnomaly) ? 0.6 : 0.0;
        dims[RadarDimensions.SecurityTool] = sink.ReadBoolHint(SignalKeys.SecurityToolDetected) ? 0.9 : 0.0;
        dims[RadarDimensions.ClientFingerprint] = Math.Min(1.0, sink.ReadDoubleHint(SignalKeys.FingerprintHeadlessScore));
        dims[RadarDimensions.VersionAge] = Math.Min(1.0, sink.ReadDoubleHint(SignalKeys.BrowserVersionAge) / 365.0);
        dims[RadarDimensions.Inconsistency] = Math.Min(1.0, sink.ReadDoubleHint(SignalKeys.InconsistencyScore));
        dims[RadarDimensions.ReputationMatch] = sink.ReadBoolHint(SignalKeys.ReputationFastPathHit) ? 0.7 : 0.0;
        dims[RadarDimensions.AiClassification] = Math.Min(1.0, sink.ReadDoubleHint(SignalKeys.AiConfidence));
        dims[RadarDimensions.ClusterSignal] = Math.Min(1.0, Math.Max(
            sink.ReadDoubleHint(SignalKeys.ClusterAvgSimilarity),
            sink.ReadDoubleHint("cluster.community_affinity")));
        dims[RadarDimensions.CountryReputation] = Math.Min(1.0, sink.ReadDoubleHint(SignalKeys.GeoCountryBotRate));
        dims[RadarDimensions.RatePattern] = sink.ReadBoolHint(SignalKeys.BehavioralRateExceeded) ? 0.8 : 0.0;
        dims[RadarDimensions.PayloadSignature] = Math.Min(1.0, Math.Max(
            sink.ReadBoolHint(SignalKeys.AttackDetected) ? 0.6 : 0.0,
            Math.Max(
                sink.ReadBoolHint(SignalKeys.AttackSqli) || sink.ReadBoolHint(SignalKeys.AttackCmdi) ||
                sink.ReadBoolHint(SignalKeys.AttackSsrf) || sink.ReadBoolHint(SignalKeys.AttackSsti) ||
                sink.ReadBoolHint(SignalKeys.AttackXss) ? 0.95 : 0.0,
                sink.ReadBoolHint(SignalKeys.AttackPathProbe) || sink.ReadBoolHint(SignalKeys.AttackConfigExposure) ||
                sink.ReadBoolHint(SignalKeys.AttackAdminScan) || sink.ReadBoolHint(SignalKeys.AttackWebshellProbe) ||
                sink.ReadBoolHint(SignalKeys.AttackBackupScan) || sink.ReadBoolHint(SignalKeys.AttackDebugExposure)
                    ? 0.7
                    : 0.0)));

        return dims;
    }
}
