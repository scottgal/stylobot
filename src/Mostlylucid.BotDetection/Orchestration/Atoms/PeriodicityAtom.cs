using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects periodic patterns in
///     request timing revealing bot behaviour: fixed-interval polling,
///     time-of-day consistency, cron-like schedules. Periodicity survives
///     identity rotation -- a bot rotating IPs every 5 minutes has a
///     ROTATION PERIOD that is itself identifying.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>PeriodicityContributor</c>. Priority 25.
///     </para>
///     <para>
///         Per-signature timestamp history in <see cref="IMemoryCache"/>
///         keyed by primary signature -- same identifier grade as fingerprint
///         IDs already in the sink. The sink learns statistical results (CV,
///         mean interval, dominant period, peak strength, hour entropy) --
///         numeric summaries, not the timestamp series.
///     </para>
///     <para>
///         Inline <c>SequenceGuardTrigger.Default</c> port. RequiredSignals
///         [<see cref="SignalKeys.PrimarySignature"/>].
///     </para>
/// </remarks>
public sealed class PeriodicityAtom : DetectorAtomBase
{
    private const string CachePrefix = "periodicity:";
    private const int MaxHistory = 200;
    private const int SequenceMinPosition = 3;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(30);

    private readonly ILogger<PeriodicityAtom> _logger;
    private readonly IMemoryCache _cache;
    private readonly IDetectorConfigProvider _configProvider;

    public PeriodicityAtom(
        ILogger<PeriodicityAtom> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache)
        : base(name: "Periodicity", category: "Periodicity")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
    }

    public override int Priority => 25;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    private double RegularityBotThreshold => _configProvider.GetParameter(Name, "regularity_bot_threshold", 0.7);
    private double RegularityBotConfidence => _configProvider.GetParameter(Name, "regularity_bot_confidence", 0.35);
    private double CronPatternBotConfidence => _configProvider.GetParameter(Name, "cron_pattern_bot_confidence", 0.4);
    private double HumanRhythmConfidence => _configProvider.GetParameter(Name, "human_rhythm_confidence", -0.15);
    private int MinRequestsForAnalysis => _configProvider.GetParameter(Name, "min_requests", 10);
    private int AutocorrelationMaxLag => _configProvider.GetParameter(Name, "max_lag", 50);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!ShouldRunUnderSequenceGuard(sink))
            return Task.FromResult(None());

        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult(None());

        var now = DateTimeOffset.UtcNow;
        var history = RecordTimestamp(signature, now);

        if (history.Count < MinRequestsForAnalysis)
            return Task.FromResult(None());

        var intervals = new List<double>(history.Count - 1);
        for (var i = 1; i < history.Count; i++)
            intervals.Add((history[i] - history[i - 1]).TotalSeconds);

        var (cv, meanInterval) = ComputeCoefficientOfVariation(intervals);
        var (dominantPeriod, peakStrength) = FindDominantPeriod(intervals);
        var hourEntropy = ComputeHourEntropy(history);

        sink.Raise($"{SignalKeys.PeriodicityCV}:{cv.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.PeriodicityMeanInterval}:{meanInterval.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.PeriodicityDominantPeriod}:{dominantPeriod}", sessionId);
        sink.Raise($"{SignalKeys.PeriodicityPeakStrength}:{peakStrength.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.PeriodicityHourEntropy}:{hourEntropy.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        var sequenceOnTrack = sink.ReadBoolHint(SignalKeys.SequenceOnTrack);
        var sequenceDiverged = sink.ReadBoolHint(SignalKeys.SequenceDiverged);

        var contributions = new List<DetectionContribution>();

        if (cv < 0.15 && intervals.Count >= MinRequestsForAnalysis && !sequenceOnTrack)
        {
            var confidence = sequenceDiverged
                ? RegularityBotConfidence * 1.25 * (1.0 - cv / 0.15)
                : RegularityBotConfidence * (1.0 - cv / 0.15);

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "PeriodicPolling",
                ConfidenceDelta = confidence,
                Weight = 1.0,
                Reason = sequenceDiverged
                    ? $"Fixed-interval polling confirmed by content-sequence divergence: mean={meanInterval:F1}s, CV={cv:F3}"
                    : $"Fixed-interval polling detected: mean={meanInterval:F1}s, CV={cv:F3}",
                BotType = BotType.Scraper.ToString()
            });

            _logger.LogDebug("Periodic polling: {Sig} mean={Mean:F1}s CV={CV:F3}",
                signature[..Math.Min(8, signature.Length)], meanInterval, cv);
        }

        if (peakStrength > 0.5 && dominantPeriod > 1 && !sequenceOnTrack)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CronSchedule",
                ConfidenceDelta = CronPatternBotConfidence * peakStrength,
                Weight = 1.0,
                Reason = $"Periodic schedule detected: period={dominantPeriod * meanInterval:F0}s, strength={peakStrength:F2}",
                BotType = BotType.MonitoringBot.ToString()
            });
        }

        if (hourEntropy < 1.5 && history.Count >= 20)
            sink.Raise("periodicity.temporal_concentration:true", sessionId);

        if (cv > 0.8 && hourEntropy is > 2.0 and < 3.5 && intervals.Count >= MinRequestsForAnalysis)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "NaturalRhythm",
                ConfidenceDelta = HumanRhythmConfidence,
                Weight = 1.0,
                Reason = "Irregular timing with natural daily rhythm"
            });
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "Insufficient data for temporal pattern"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static bool ShouldRunUnderSequenceGuard(SignalSink sink)
    {
        var positionHint = sink.ReadHint(SignalKeys.SequencePosition);
        if (positionHint is null) return true;
        if (!sink.ReadBoolHint(SignalKeys.SequenceOnTrack, fallback: true)) return true;
        if (sink.ReadBoolHint(SignalKeys.SequenceDiverged)) return true;
        return int.TryParse(positionHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pos)
               && pos >= SequenceMinPosition;
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

    private (int DominantLag, double PeakStrength) FindDominantPeriod(List<double> intervals)
    {
        if (intervals.Count < 5) return (0, 0);
        var mean = intervals.Average();
        var maxLag = Math.Min(AutocorrelationMaxLag, intervals.Count / 2);
        var denom = intervals.Sum(x => (x - mean) * (x - mean));
        if (denom < 0.001) return (0, 0);

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
