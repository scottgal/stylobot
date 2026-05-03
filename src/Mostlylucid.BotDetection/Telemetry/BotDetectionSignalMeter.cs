using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mostlylucid.BotDetection.Orchestration;
using DetectionContribution = Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution;

namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Signal-adapter metrics for Prometheus/Grafana dashboards.
///     Records high-level detection outcomes, geo data, detector performance,
///     score escalation, and attack intelligence from <see cref="AggregatedEvidence" />.
/// </summary>
public sealed class BotDetectionSignalMeter : IDisposable
{
    public const string MeterName = "Mostlylucid.BotDetection.Signals";

    private readonly Meter _meter;

    // Counters
    private readonly Counter<long> _detectionsTotal;
    private readonly Counter<long> _detectorRunsTotal;
    private readonly Counter<long> _attacksTotal;
    private readonly Counter<long> _verifiedBotsTotal;
    private readonly Counter<long> _earlyExitsTotal;
    private readonly Counter<long> _scoreEscalationTotal;
    private readonly Counter<long> _responseBoostTotal;
    private readonly Counter<long> _countryRequestsTotal;

    // Histograms
    private readonly Histogram<double> _detectionDuration;
    private readonly Histogram<double> _detectionConfidence;
    private readonly Histogram<double> _detectionBotProbability;
    private readonly Histogram<double> _detectorDuration;
    private readonly Histogram<int> _signalsPerRequest;
    private readonly Histogram<double> _detectorContribution;
    private readonly Histogram<double> _detectionWaveScore;

    public BotDetectionSignalMeter(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName, "1.0.0");

        // ── Counters ──────────────────────────────────────────────────
        _detectionsTotal = _meter.CreateCounter<long>(
            "stylobot_detections_total",
            "{detection}",
            "Total bot detection decisions");

        _detectorRunsTotal = _meter.CreateCounter<long>(
            "stylobot_detectors_runs_total",
            "{run}",
            "Individual detector execution count");

        _attacksTotal = _meter.CreateCounter<long>(
            "stylobot_attacks_total",
            "{attack}",
            "Attack detections by category");

        _verifiedBotsTotal = _meter.CreateCounter<long>(
            "stylobot_verified_bots_total",
            "{bot}",
            "Verified and spoofed bot counts");

        _earlyExitsTotal = _meter.CreateCounter<long>(
            "stylobot_early_exits_total",
            "{exit}",
            "Fast-path early exit verdicts");

        _scoreEscalationTotal = _meter.CreateCounter<long>(
            "stylobot_score_escalation_total",
            "{escalation}",
            "Risk band transitions during detection");

        _responseBoostTotal = _meter.CreateCounter<long>(
            "stylobot_response_boost_total",
            "{boost}",
            "Response-based score escalations");

        _countryRequestsTotal = _meter.CreateCounter<long>(
            "stylobot_country_requests_total",
            "{request}",
            "Requests by country for Grafana world map");

        // ── Histograms ────────────────────────────────────────────────
        _detectionDuration = _meter.CreateHistogram<double>(
            "stylobot_detection_duration_seconds",
            "s",
            "Full detection pipeline latency");

        _detectionConfidence = _meter.CreateHistogram<double>(
            "stylobot_detection_confidence",
            "1",
            "Detection confidence distribution");

        _detectionBotProbability = _meter.CreateHistogram<double>(
            "stylobot_detection_bot_probability",
            "1",
            "Bot probability score distribution");

        _detectorDuration = _meter.CreateHistogram<double>(
            "stylobot_detector_duration_seconds",
            "s",
            "Per-detector execution latency");

        _signalsPerRequest = _meter.CreateHistogram<int>(
            "stylobot_signals_per_request",
            "{signal}",
            "Number of signals emitted per request");

        _detectorContribution = _meter.CreateHistogram<double>(
            "stylobot_detector_contribution",
            "1",
            "Weighted score delta per detector");

        _detectionWaveScore = _meter.CreateHistogram<double>(
            "stylobot_detection_wave_score",
            "1",
            "Cumulative score at each wave boundary");
    }

    /// <summary>
    ///     Record the primary detection outcome.
    /// </summary>
    public void RecordDetection(AggregatedEvidence evidence, string? country = null)
    {
        var isBot = evidence.BotProbability >= 0.5;
        var riskBand = evidence.RiskBand.ToString();
        var botType = evidence.PrimaryBotType?.ToString() ?? "unknown";
        var action = evidence.TriggeredActionPolicyName ?? "none";

        _detectionsTotal.Add(1,
            new("risk_band", riskBand),
            new("bot_type", botType),
            new("action", action),
            new("country", country ?? "unknown"),
            new("is_bot", isBot.ToString().ToLowerInvariant()));

        _detectionDuration.Record(evidence.TotalProcessingTimeMs / 1000.0,
            new("policy", evidence.PolicyName ?? "default"),
            new("early_exit", evidence.EarlyExit.ToString().ToLowerInvariant()));

        _detectionConfidence.Record(evidence.Confidence,
            new TagList { new("risk_band", riskBand) });

        _detectionBotProbability.Record(evidence.BotProbability,
            new TagList { new("bot_type", botType) });

        _signalsPerRequest.Record(evidence.Signals?.Count ?? 0);

        // Country geo metric for Grafana world map
        if (!string.IsNullOrEmpty(country) && country != "unknown")
        {
            _countryRequestsTotal.Add(1,
                new("country", country),
                new("is_bot", isBot.ToString().ToLowerInvariant()));
        }

        // Early exit tracking
        if (evidence.EarlyExit && evidence.EarlyExitVerdict.HasValue)
        {
            _earlyExitsTotal.Add(1,
                new TagList { new("verdict", evidence.EarlyExitVerdict.Value.ToString()) });
        }

        // Verified bot tracking
        if (evidence.Signals != null)
        {
            if (evidence.Signals.TryGetValue("verifiedbot.confirmed", out var verified) && verified is true)
            {
                var botName = evidence.PrimaryBotName ?? "unknown";
                var spoofed = evidence.Signals.TryGetValue("verifiedbot.spoofed", out var s) && s is true;
                _verifiedBotsTotal.Add(1,
                    new("bot_name", botName),
                    new("verified", (!spoofed).ToString().ToLowerInvariant()));
            }

            // Attack tracking
            if (evidence.Signals.TryGetValue("attack.detected", out var attacked) && attacked is true)
            {
                var categories = evidence.Signals.TryGetValue("attack.categories", out var cats)
                    ? cats?.ToString() ?? "unknown"
                    : "unknown";
                _attacksTotal.Add(1, new TagList { new("category", categories) });
            }
        }
    }

    /// <summary>
    ///     Record per-detector execution from contributions.
    /// </summary>
    public void RecordDetectorRuns(IReadOnlyList<DetectionContribution> contributions)
    {
        var cumulativeScore = 0.0;
        var lastWave = -1;

        foreach (var c in contributions)
        {
            var effective = c.ConfidenceDelta * c.Weight;
            cumulativeScore += effective;

            _detectorRunsTotal.Add(1,
                new("detector", c.DetectorName),
                new("outcome", c.ConfidenceDelta > 0 ? "bot" : c.ConfidenceDelta < 0 ? "human" : "neutral"));

            _detectorDuration.Record(c.ProcessingTimeMs / 1000.0,
                new("detector", c.DetectorName),
                new("wave", c.Priority.ToString()));

            var direction = c.ConfidenceDelta > 0 ? "bot" : c.ConfidenceDelta < 0 ? "human" : "neutral";
            _detectorContribution.Record(Math.Abs(effective),
                new("detector", c.DetectorName),
                new("direction", direction));

            // Wave score tracking (emit when wave boundary changes)
            if (c.Priority != lastWave && lastWave >= 0)
            {
                _detectionWaveScore.Record(cumulativeScore,
                    new TagList { new("wave", lastWave.ToString()) });
            }

            lastWave = c.Priority;
        }

        // Final wave
        if (lastWave >= 0)
        {
            _detectionWaveScore.Record(cumulativeScore,
                new TagList { new("wave", lastWave.ToString()) });
        }
    }

    /// <summary>
    ///     Record a risk band escalation (e.g., Low → High).
    /// </summary>
    public void RecordEscalation(RiskBand fromBand, RiskBand toBand, string? boostReason = null)
    {
        _scoreEscalationTotal.Add(1,
            new("from_band", fromBand.ToString()),
            new("to_band", toBand.ToString()));

        if (!string.IsNullOrEmpty(boostReason))
        {
            _responseBoostTotal.Add(1,
                new("from_band", fromBand.ToString()),
                new("to_band", toBand.ToString()),
                new("boost_reason", boostReason));
        }
    }

    public void Dispose() => _meter.Dispose();
}
