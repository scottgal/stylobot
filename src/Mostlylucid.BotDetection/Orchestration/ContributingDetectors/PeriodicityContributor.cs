using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects periodic patterns in request timing that reveal bot behavior:
///     - Request cadence: fixed-interval polling (e.g., every 30s)
///     - Temporal patterns: time-of-day consistency across sessions
///     - Rotation cadence: how often identity factors change
///
///     Periodicity is orthogonal to fingerprinting - it survives identity rotation.
///     A bot rotating IPs every 5 minutes has a ROTATION PERIOD that is itself identifying.
///
///     Uses autocorrelation to detect dominant frequencies in the inter-request interval series.
/// </summary>
public class PeriodicityContributor : ConfiguredContributorBase
{
    private readonly ILogger<PeriodicityContributor> _logger;
    private readonly IMemoryCache _cache;

    private const string CachePrefix = "periodicity:";
    private const int MaxHistory = 200;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(30);

    // Configurable via YAML
    private double RegularityBotThreshold => GetParam("regularity_bot_threshold", 0.7);
    private double RegularityBotConfidence => GetParam("regularity_bot_confidence", 0.35);
    private double CronPatternBotConfidence => GetParam("cron_pattern_bot_confidence", 0.4);
    private double HumanRhythmConfidence => GetParam("human_rhythm_confidence", -0.15);
    private int MinRequestsForAnalysis => GetParam("min_requests", 10);
    private int AutocorrelationMaxLag => GetParam("max_lag", 50);

    public PeriodicityContributor(
        ILogger<PeriodicityContributor> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache) : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "Periodicity";
    public override int Priority => 25; // After behavioral waveform (3), before session vector (30)

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.PrimarySignature),
        SequenceGuardTrigger.Default
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = new Dictionary<string, object>();

        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature)) return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Record this request's timestamp
        var now = DateTimeOffset.UtcNow;
        var history = RecordTimestamp(signature, now);

        if (history.Count < MinRequestsForAnalysis)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Compute inter-request intervals (in seconds)
        var intervals = new List<double>(history.Count - 1);
        for (var i = 1; i < history.Count; i++)
            intervals.Add((history[i] - history[i - 1]).TotalSeconds);

        // === Analysis 1: Request cadence regularity ===
        var (cv, meanInterval) = ComputeCoefficientOfVariation(intervals);
        signals[SignalKeys.PeriodicityCV] = cv;
        signals[SignalKeys.PeriodicityMeanInterval] = meanInterval;

        // === Analysis 2: Autocorrelation - find dominant periodic signal ===
        var (dominantPeriod, peakStrength) = FindDominantPeriod(intervals);
        signals[SignalKeys.PeriodicityDominantPeriod] = dominantPeriod;
        signals[SignalKeys.PeriodicityPeakStrength] = peakStrength;

        // === Analysis 3: Hour-of-day entropy ===
        var hourEntropy = ComputeHourEntropy(history);
        signals[SignalKeys.PeriodicityHourEntropy] = hourEntropy;

        // Write signals to blackboard
        foreach (var (key, value) in signals)
            state.WriteSignal(key, value);

        // Read sequence signals - ContentSequenceContributor (Priority 4) runs before this (Priority 25).
        var sequenceOnTrack = state.GetSignal<bool?>(SignalKeys.SequenceOnTrack) ?? false;
        var sequenceDiverged = state.GetSignal<bool?>(SignalKeys.SequenceDiverged) ?? false;

        // === Scoring ===

        // Very regular intervals (low CV) = bot-like polling.
        // Suppress if sequence is on-track: legitimate apps poll APIs at fixed intervals
        // after a valid page load (notification checks, heartbeats, presence signals).
        if (cv < 0.15 && intervals.Count >= MinRequestsForAnalysis && !sequenceOnTrack)
        {
            var confidence = sequenceDiverged
                ? RegularityBotConfidence * 1.25 * (1.0 - cv / 0.15)
                : RegularityBotConfidence * (1.0 - cv / 0.15);

            contributions.Add(BotContribution(
                "PeriodicPolling",
                sequenceDiverged
                    ? $"Fixed-interval polling confirmed by content-sequence divergence: mean={meanInterval:F1}s, CV={cv:F3}"
                    : $"Fixed-interval polling detected: mean={meanInterval:F1}s, CV={cv:F3}",
                confidenceOverride: confidence,
                botType: BotType.Scraper.ToString()));

            _logger.LogDebug("Periodic polling: {Sig} mean={Mean:F1}s CV={CV:F3}",
                signature[..Math.Min(8, signature.Length)], meanInterval, cv);
        }

        // Strong autocorrelation peak = cron-like schedule.
        // Suppress if on-track: legitimate apps can have periodic interaction patterns.
        if (peakStrength > 0.5 && dominantPeriod > 1 && !sequenceOnTrack)
        {
            contributions.Add(BotContribution(
                "CronSchedule",
                $"Periodic schedule detected: period={dominantPeriod * meanInterval:F0}s, strength={peakStrength:F2}",
                confidenceOverride: CronPatternBotConfidence * peakStrength,
                botType: BotType.MonitoringBot.ToString()));
        }

        // Very low hour entropy = always active at same times (cron job)
        if (hourEntropy < 1.5 && history.Count >= 20)
            state.WriteSignal("periodicity.temporal_concentration", true);

        // Human signal: high CV (irregular) + moderate hour entropy (natural daily rhythm)
        if (cv > 0.8 && hourEntropy is > 2.0 and < 3.5 && intervals.Count >= MinRequestsForAnalysis)
        {
            contributions.Add(HumanContribution(
                "NaturalRhythm",
                "Irregular timing with natural daily rhythm"));
        }

        // If no contributions, emit neutral
        if (contributions.Count == 0)
            contributions.Add(NeutralContribution("Periodicity", "Insufficient data for temporal pattern"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private List<DateTimeOffset> RecordTimestamp(string signature, DateTimeOffset timestamp)
    {
        var key = $"{CachePrefix}{signature}";
        var history = _cache.Get<List<DateTimeOffset>>(key) ?? new List<DateTimeOffset>();

        history.Add(timestamp);
        if (history.Count > MaxHistory)
            history.RemoveRange(0, history.Count - MaxHistory);

        _cache.Set(key, history, new MemoryCacheEntryOptions { SlidingExpiration = CacheExpiration });
        return history;
    }

    private static (double CV, double MeanInterval) ComputeCoefficientOfVariation(List<double> intervals)
    {
        if (intervals.Count < 2) return (1.0, 0);
        var mean = intervals.Average();
        if (mean < 0.001) return (0, 0);
        var stddev = Math.Sqrt(intervals.Select(x => (x - mean) * (x - mean)).Average());
        return (stddev / mean, mean);
    }

    /// <summary>
    ///     Find the dominant period via autocorrelation.
    ///     Returns (lag of peak, peak strength 0-1).
    /// </summary>
    private (int DominantLag, double PeakStrength) FindDominantPeriod(List<double> intervals)
    {
        if (intervals.Count < 5) return (0, 0);

        var mean = intervals.Average();
        var maxLag = Math.Min(AutocorrelationMaxLag, intervals.Count / 2);

        // Compute autocorrelation at lag 0 (denominator)
        var denom = intervals.Sum(x => (x - mean) * (x - mean));
        if (denom < 0.001) return (0, 0); // All intervals identical - perfect periodicity at lag 1

        var bestLag = 0;
        var bestCorr = 0.0;

        for (var lag = 2; lag <= maxLag; lag++)
        {
            var sum = 0.0;
            for (var i = 0; i < intervals.Count - lag; i++)
                sum += (intervals[i] - mean) * (intervals[i + lag] - mean);

            var corr = sum / denom;
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        return (bestLag, bestCorr);
    }

    /// <summary>
    ///     Compute Shannon entropy of hour-of-day distribution (0-24 bins).
    ///     Low entropy = concentrated in few hours (scheduled).
    ///     Max entropy ≈ 4.58 (uniform across all hours).
    /// </summary>
    private static double ComputeHourEntropy(List<DateTimeOffset> timestamps)
    {
        var hourCounts = new int[24];
        foreach (var ts in timestamps)
            hourCounts[ts.Hour]++;

        var total = (double)timestamps.Count;
        var entropy = 0.0;
        for (var h = 0; h < 24; h++)
        {
            if (hourCounts[h] == 0) continue;
            var p = hourCounts[h] / total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}