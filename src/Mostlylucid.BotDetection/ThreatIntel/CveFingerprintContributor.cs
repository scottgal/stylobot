using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Contributing detector that matches the current session's radar shape against
///     CVE-derived fingerprints. Runs in Wave 1 (after heuristic features are available)
///     at Priority 55 -- between Heuristic (50) and Similarity (60).
///
///     When a match is found, emits signals with CVE correlation metadata and contributes
///     a bot detection signal proportional to match confidence and advisory severity.
///     Configuration loaded from: cvefingerprint.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:CveFingerprintContributor:*
/// </summary>
public sealed class CveFingerprintContributor : ConfiguredContributorBase
{
    private readonly ICveFingerprintMatcher _matcher;
    private readonly ILogger<CveFingerprintContributor> _logger;

    public CveFingerprintContributor(
        ICveFingerprintMatcher matcher,
        ILogger<CveFingerprintContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _matcher = matcher;
        _logger = logger;
    }

    public override string Name => "CveFingerprint";
    public override int Priority => Manifest?.Priority ?? 55;

    // Config-driven thresholds
    private double MatchThreshold => GetParam("match_threshold", 0.80);
    private double CveWeight => GetParam("cve_weight", 1.5);

    // Requires heuristic to have run so we have feature signals to build the radar shape from
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new SignalExistsTrigger(SignalKeys.HeuristicPrediction)
    };

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // No fingerprints loaded -- nothing to match against
        if (_matcher.FingerprintCount == 0)
        {
            state.WriteSignal(SignalKeys.CveMatchCount, 0);
            return None();
        }

        try
        {
            // Build the current session's radar shape from blackboard signals
            var dimensions = BuildRadarDimensions(state);

            // Find matching CVE fingerprints
            var matches = await _matcher.FindMatchesAsync(
                dimensions, topK: 5, minSimilarity: MatchThreshold, cancellationToken);

            state.WriteSignal(SignalKeys.CveMatchCount, matches.Count);

            if (matches.Count == 0)
                return None();

            var topMatch = matches[0];
            state.WriteSignal(SignalKeys.CveTopAdvisoryId, topMatch.AdvisoryId);
            state.WriteSignal(SignalKeys.CveTopSimilarity, topMatch.Similarity);
            state.WriteSignal(SignalKeys.CveTopSeverity, topMatch.Severity);

            if (topMatch.ClusterLabel is not null)
                state.WriteSignal(SignalKeys.CveClusterLabel, topMatch.ClusterLabel);

            // Write all matched CVE IDs for telemetry
            state.WriteSignal(SignalKeys.CveMatchedIds,
                string.Join(",", matches.Select(m => m.AdvisoryId)));

            // Calculate confidence boost based on severity and similarity
            var severityBoost = topMatch.Severity switch
            {
                "critical" => GetParam("severity_boost_critical", 0.35),
                "high" => GetParam("severity_boost_high", 0.25),
                "medium" => GetParam("severity_boost_medium", 0.15),
                "low" => GetParam("severity_boost_low", 0.08),
                _ => GetParam("severity_boost_default", 0.10)
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
                weight: CveWeight, // High weight -- CVE matches are strong evidence
                botType: BotType.ExploitScanner.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CVE fingerprint matching failed");
            state.WriteSignal("cve.error", ex.Message);
            return None();
        }
    }

    /// <summary>
    ///     Build the 16-dimension radar shape from current blackboard signals.
    ///     Maps detector signals to the standardised RadarDimensions.
    /// </summary>
    private static Dictionary<string, double> BuildRadarDimensions(BlackboardState state)
    {
        var s = state.Signals;
        var dims = RadarDimensions.CreateEmpty();

        dims[RadarDimensions.UaAnomaly] = ReadBool(s, SignalKeys.UserAgentIsBot) ? 0.8 : 0.0;
        dims[RadarDimensions.HeaderAnomaly] =
            ReadBool(s, SignalKeys.HeadersSuspicious) || ReadBool(s, SignalKeys.HeadersMissing) ? 0.6 : 0.0;
        dims[RadarDimensions.IpReputation] = ReadBool(s, SignalKeys.IpIsDatacenter) ? 0.5 : 0.0;
        dims[RadarDimensions.Behavioral] = ReadBool(s, SignalKeys.BehavioralAnomalyDetected) ? 0.7 : 0.0;
        dims[RadarDimensions.AdvancedBehavioral] = Math.Min(1.0, Math.Max(
            ReadBool(s, SignalKeys.StreamHandshakeStorm) ? 0.9 : 0.0,
            Math.Max(
                ReadBool(s, SignalKeys.StreamCrossEndpointMixing) ? 0.8 : 0.0,
                Math.Max(
                    ReadDouble(s, SignalKeys.WaveformTimingRegularity),
                    Math.Max(
                        ReadDouble(s, SignalKeys.SessionVelocityMagnitude),
                        ReadBool(s, SignalKeys.WaveformBurstDetected) ? 0.75 : 0.0)))));
        dims[RadarDimensions.CacheBehavior] = ReadBool(s, SignalKeys.CacheBehaviorAnomaly) ? 0.6 : 0.0;
        dims[RadarDimensions.SecurityTool] = ReadBool(s, SignalKeys.SecurityToolDetected) ? 0.9 : 0.0;
        dims[RadarDimensions.ClientFingerprint] = Math.Min(1.0, ReadDouble(s, SignalKeys.FingerprintHeadlessScore));
        dims[RadarDimensions.VersionAge] = Math.Min(1.0, ReadDouble(s, SignalKeys.BrowserVersionAge) / 365.0);
        dims[RadarDimensions.Inconsistency] = Math.Min(1.0, ReadDouble(s, SignalKeys.InconsistencyScore));
        dims[RadarDimensions.ReputationMatch] = ReadBool(s, SignalKeys.ReputationFastPathHit) ? 0.7 : 0.0;
        dims[RadarDimensions.AiClassification] = Math.Min(1.0, ReadDouble(s, SignalKeys.AiConfidence));
        dims[RadarDimensions.ClusterSignal] = Math.Min(1.0, Math.Max(
            ReadDouble(s, SignalKeys.ClusterAvgSimilarity),
            ReadDouble(s, "cluster.community_affinity")));
        dims[RadarDimensions.CountryReputation] = Math.Min(1.0, ReadDouble(s, SignalKeys.GeoCountryBotRate));
        dims[RadarDimensions.RatePattern] = ReadBool(s, SignalKeys.BehavioralRateExceeded) ? 0.8 : 0.0;
        dims[RadarDimensions.PayloadSignature] = Math.Min(1.0, Math.Max(
            ReadBool(s, SignalKeys.AttackDetected) ? 0.6 : 0.0,
            Math.Max(
                ReadBool(s, SignalKeys.AttackSqli) || ReadBool(s, SignalKeys.AttackCmdi) ||
                ReadBool(s, SignalKeys.AttackSsrf) || ReadBool(s, SignalKeys.AttackSsti) ||
                ReadBool(s, SignalKeys.AttackXss) ? 0.95 : 0.0,
                ReadBool(s, SignalKeys.AttackPathProbe) || ReadBool(s, SignalKeys.AttackConfigExposure) ||
                ReadBool(s, SignalKeys.AttackAdminScan) || ReadBool(s, SignalKeys.AttackWebshellProbe) ||
                ReadBool(s, SignalKeys.AttackBackupScan) || ReadBool(s, SignalKeys.AttackDebugExposure)
                    ? 0.7
                    : 0.0)));

        return dims;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object> signals, string key) =>
        signals.TryGetValue(key, out var val) && val switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => false
        };

    private static double ReadDouble(IReadOnlyDictionary<string, object> signals, string key)
    {
        if (!signals.TryGetValue(key, out var val))
            return 0.0;

        return val switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => 0.0
        };
    }
}
