using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects reactive bot patterns:
///     how a client behaves AFTER 4xx / 429 responses. Automated retry logic
///     is MECHANICAL -- gap intervals are predictable, gap ratios are
///     consistent, and Retry-After compliance is measurable in milliseconds.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ReactivePatternContributor</c>. Priority 32.
///     </para>
///     <para>
///         Cross-request error history lives on the shared
///         <see cref="ReactiveSignalTracker"/> singleton (already the
///         contributor's storage). The atom emits per-request numeric
///         signals to the sink as Model-2 hints so downstream atoms can
///         read them without touching the tracker.
///     </para>
///     <para>
///         RequiredSignals [<see cref="SignalKeys.PrimarySignature"/>] --
///         reactive analysis is per-identity so we need the signature.
///     </para>
/// </remarks>
public sealed class ReactivePatternAtom : DetectorAtomBase
{
    private static readonly (double Base, string Name)[] KnownBases =
    [
        (2.0, "exponential"),
        (1.618, "fibonacci"),
        (1.5, "mild_exponential"),
        (1.0, "linear")
    ];

    private readonly ILogger<ReactivePatternAtom> _logger;
    private readonly ReactiveSignalTracker _tracker;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ReactivePatternAtom(
        ILogger<ReactivePatternAtom> logger,
        IDetectorConfigProvider configProvider,
        ReactiveSignalTracker tracker,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "ReactivePattern", category: "ReactivePattern")
    {
        _logger = logger;
        _tracker = tracker;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 32;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    private int MinErrorEventsForAnalysis => _configProvider.GetParameter(Name, "min_error_events", 2);
    private double CompliancePrecisionThreshold => _configProvider.GetParameter(Name, "compliance_precision_threshold", 0.15);
    private double ComplianceBotConfidence => _configProvider.GetParameter(Name, "compliance_bot_confidence", 0.45);
    private double GeometricCvThreshold => _configProvider.GetParameter(Name, "geometric_cv_threshold", 0.25);
    private double GeometricBotConfidence => _configProvider.GetParameter(Name, "geometric_bot_confidence", 0.5);
    private int GeometricMinSteps => _configProvider.GetParameter(Name, "geometric_min_steps", 3);
    private double PathPersistenceThreshold => _configProvider.GetParameter(Name, "path_persistence_threshold", 0.6);
    private double PathPersistenceBotConfidence => _configProvider.GetParameter(Name, "path_persistence_bot_confidence", 0.4);
    private int CoordinatedRetryMinSignatures => _configProvider.GetParameter(Name, "coordinated_retry_min_signatures", 3);
    private double CoordinatedRetryBotConfidence => _configProvider.GetParameter(Name, "coordinated_retry_bot_confidence", 0.35);
    private double RateAdaptedBotConfidence => _configProvider.GetParameter(Name, "rate_adapted_bot_confidence", 0.3);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult(None());

        var history = _tracker.GetHistory(signature);
        var now = DateTimeOffset.UtcNow;

        sink.Raise($"{SignalKeys.ReactiveErrorEventCount}:{history.Count}", sessionId);

        if (history.Count < MinErrorEventsForAnalysis)
            return Task.FromResult(Single(DetectionContribution.Info(Name, Category, "No prior error events to analyze")));

        var lastEvent = history[^1];
        sink.Raise(
            $"{SignalKeys.ReactivePost4xxGapMs}:{((float)(now - lastEvent.ServedAt).TotalMilliseconds).ToString("F2", CultureInfo.InvariantCulture)}",
            sessionId);

        var contributions = new List<DetectionContribution>();
        AnalyzeRetryAfterCompliance(history, now, sink, sessionId, contributions);
        AnalyzePathPersistence(history, sink, sessionId, contributions);
        AnalyzeGeometricRetry(history, sink, sessionId, contributions);
        AnalyzeRateAdaptation(history, sink, sessionId, contributions);
        AnalyzeCoordinatedRetry(history, now, sink, sessionId, contributions);

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "No reactive bot patterns detected"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private void AnalyzeRetryAfterCompliance(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        DateTimeOffset now,
        SignalSink sink,
        string sessionId,
        List<DetectionContribution> contributions)
    {
        var lastThrottle = history
            .Where(e => e.StatusCode == 429 && e.RetryAfterSeconds.HasValue)
            .OrderByDescending(e => e.ServedAt)
            .FirstOrDefault();

        if (lastThrottle == default)
        {
            sink.Raise($"{SignalKeys.ReactiveRetryAfterCompliance}:-1", sessionId);
            return;
        }

        var retryAfterMs = lastThrottle.RetryAfterSeconds!.Value * 1000.0;
        var actualGapMs = (now - lastThrottle.ServedAt).TotalMilliseconds;

        if (retryAfterMs <= 0)
        {
            sink.Raise($"{SignalKeys.ReactiveRetryAfterCompliance}:-1", sessionId);
            return;
        }

        var compliance = actualGapMs / retryAfterMs;
        sink.Raise($"{SignalKeys.ReactiveRetryAfterCompliance}:{((float)compliance).ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        var deviation = Math.Abs(compliance - 1.0);
        if (deviation < CompliancePrecisionThreshold && compliance > 0.9)
        {
            _logger.LogDebug("Retry-After compliance: ratio={Compliance:F3} (deviation={Dev:F3})",
                compliance, deviation);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "RetryCompliance",
                ConfidenceDelta = ComplianceBotConfidence,
                Weight = 1.0,
                Reason = $"Retry-After compliance ratio {compliance:F2} (deviation {deviation:F2}): inhuman timing precision",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzePathPersistence(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        SignalSink sink,
        string sessionId,
        List<DetectionContribution> contributions)
    {
        var forbidden = history.Where(e => e.StatusCode == 403).ToList();
        if (forbidden.Count == 0)
        {
            sink.Raise($"{SignalKeys.ReactivePathPersistencePost403}:0", sessionId);
            sink.Raise($"{SignalKeys.ReactivePathPersistenceRatio}:0", sessionId);
            return;
        }

        var blockedPaths = forbidden.Select(e => e.Path).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentPath = _httpContextAccessor.HttpContext?.Request.Path.Value ?? string.Empty;
        var isPersisting = blockedPaths.Contains(currentPath);
        var persistenceRatio = history.Count(e => blockedPaths.Contains(e.Path)) / (double)history.Count;

        sink.Raise($"{SignalKeys.ReactivePathPersistencePost403}:{(isPersisting ? "1" : "0")}", sessionId);
        sink.Raise($"{SignalKeys.ReactivePathPersistenceRatio}:{((float)persistenceRatio).ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        if (isPersisting && persistenceRatio >= PathPersistenceThreshold)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "PathPersistence",
                ConfidenceDelta = PathPersistenceBotConfidence,
                Weight = 1.0,
                Reason = $"Path persistence post-403: retrying blocked path '{currentPath}' ({persistenceRatio:P0} of error events on blocked paths)",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeGeometricRetry(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        SignalSink sink,
        string sessionId,
        List<DetectionContribution> contributions)
    {
        if (history.Count < GeometricMinSteps + 1)
        {
            sink.Raise($"{SignalKeys.ReactiveGeometricRatioCv}:-1", sessionId);
            sink.Raise($"{SignalKeys.ReactiveBackoffBase}:0", sessionId);
            sink.Raise($"{SignalKeys.ReactiveBackoffPattern}:none", sessionId);
            return;
        }

        var gaps = new List<double>(history.Count - 1);
        for (var i = 1; i < history.Count; i++)
        {
            var ms = (history[i].ServedAt - history[i - 1].ServedAt).TotalMilliseconds;
            if (ms > 0) gaps.Add(ms);
        }

        if (gaps.Count < GeometricMinSteps)
        {
            sink.Raise($"{SignalKeys.ReactiveGeometricRatioCv}:-1", sessionId);
            sink.Raise($"{SignalKeys.ReactiveBackoffBase}:0", sessionId);
            sink.Raise($"{SignalKeys.ReactiveBackoffPattern}:none", sessionId);
            return;
        }

        var ratios = new List<double>(gaps.Count - 1);
        for (var i = 1; i < gaps.Count; i++)
            ratios.Add(gaps[i] / gaps[i - 1]);

        var meanRatio = ratios.Average();
        var variance = ratios.Sum(r => Math.Pow(r - meanRatio, 2)) / ratios.Count;
        var cv = meanRatio > 0 ? Math.Sqrt(variance) / meanRatio : double.MaxValue;

        var (closestBase, closestName) = KnownBases
            .OrderBy(kb => Math.Abs(meanRatio - kb.Base))
            .First();
        var distToClosest = Math.Abs(meanRatio - closestBase);

        sink.Raise($"{SignalKeys.ReactiveGeometricRatioCv}:{((float)cv).ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ReactiveBackoffBase}:{((float)meanRatio).ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.ReactiveBackoffPattern}:{(distToClosest < 0.2 ? closestName : "unknown")}", sessionId);

        if (cv < GeometricCvThreshold)
        {
            _logger.LogDebug("Geometric retry pattern: mean ratio={Ratio:F2} ({Name}), CV={CV:F3}",
                meanRatio, closestName, cv);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "GeometricRetry",
                ConfidenceDelta = GeometricBotConfidence,
                Weight = 1.0,
                Reason = $"Geometric retry pattern: {closestName} backoff (ratio={meanRatio:F2}, CV={cv:F3})",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeRateAdaptation(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        SignalSink sink,
        string sessionId,
        List<DetectionContribution> contributions)
    {
        var throttleEvents = history
            .Where(e => e.StatusCode == 429)
            .OrderBy(e => e.ServedAt)
            .ToList();

        if (throttleEvents.Count < 2)
        {
            sink.Raise($"{SignalKeys.ReactiveRateAdapted}:0", sessionId);
            return;
        }

        var throttleGaps = new List<double>(throttleEvents.Count - 1);
        for (var i = 1; i < throttleEvents.Count; i++)
            throttleGaps.Add((throttleEvents[i].ServedAt - throttleEvents[i - 1].ServedAt).TotalMilliseconds);

        var increasingCount = 0;
        for (var i = 1; i < throttleGaps.Count; i++)
            if (throttleGaps[i] > throttleGaps[i - 1]) increasingCount++;

        var adaptationScore = throttleGaps.Count > 1
            ? (float)increasingCount / (throttleGaps.Count - 1)
            : 0f;

        sink.Raise($"{SignalKeys.ReactiveRateAdapted}:{adaptationScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        if (adaptationScore > 0.75f && throttleGaps.Count >= 2)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "RateAdaptation",
                ConfidenceDelta = RateAdaptedBotConfidence,
                Weight = 1.0,
                Reason = $"Rate adaptation after 429: gaps increasing ({adaptationScore:P0} monotone), automated retry with backoff",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeCoordinatedRetry(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        DateTimeOffset now,
        SignalSink sink,
        string sessionId,
        List<DetectionContribution> contributions)
    {
        if (history.Count == 0)
        {
            sink.Raise($"{SignalKeys.ReactiveCoordinatedRetry}:0", sessionId);
            sink.Raise($"{SignalKeys.ReactiveCoRetryerCount}:0", sessionId);
            return;
        }

        var recentBlockedPaths = history
            .Where(e => e.StatusCode is 403 or 429 && now - e.ServedAt < TimeSpan.FromMinutes(5))
            .Select(e => e.Path)
            .Distinct()
            .ToList();

        var maxCoRetriers = 0;
        foreach (var path in recentBlockedPaths)
        {
            var coRetriers = _tracker.GetCoRetriers(path, now - TimeSpan.FromMinutes(5));
            maxCoRetriers = Math.Max(maxCoRetriers, coRetriers.Count);
        }

        sink.Raise($"{SignalKeys.ReactiveCoordinatedRetry}:{(maxCoRetriers > 0 ? "1" : "0")}", sessionId);
        sink.Raise($"{SignalKeys.ReactiveCoRetryerCount}:{maxCoRetriers}", sessionId);

        if (maxCoRetriers >= CoordinatedRetryMinSignatures)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CoordinatedRetry",
                ConfidenceDelta = CoordinatedRetryBotConfidence,
                Weight = 1.0,
                Reason = $"Coordinated retry: {maxCoRetriers} signatures retrying same blocked paths simultaneously",
                BotType = BotType.Unknown.ToString()
            });
        }
    }
}
