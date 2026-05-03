using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
/// Detects reactive bot patterns: how a client behaves AFTER receiving 4xx/429 responses.
/// <para>
/// Humans get a 429 and stop browsing, or return much later with no fixed interval.
/// Bots implement retry logic: exponential backoff, linear backoff, Retry-After compliance.
/// The key insight is that automated retry logic is MECHANICAL - the gaps are predictable
/// and the gap ratios are consistent, while human behavior is highly variable.
/// </para>
/// <para>Signals emitted to blackboard:</para>
/// <list type="bullet">
/// <item><c>reactive.error_event_count</c> - number of prior 4xx/5xx events</item>
/// <item><c>reactive.post_4xx_gap_ms</c> - milliseconds since last error response</item>
/// <item><c>reactive.retry_after_compliance</c> - actual_gap / Retry-After (1.0 = inhuman precision)</item>
/// <item><c>reactive.path_persistence_post_403</c> - still retrying a 403'd path</item>
/// <item><c>reactive.geometric_ratio_cv</c> - CV of gap ratios (low = mechanical backoff)</item>
/// <item><c>reactive.backoff_base</c> - mean gap ratio (2.0 = exponential, 1.618 = Fibonacci)</item>
/// <item><c>reactive.rate_adapted</c> - gaps monotonically increasing after 429s</item>
/// <item><c>reactive.coordinated_retry</c> - multiple signatures retrying same blocked paths</item>
/// </list>
/// </summary>
public class ReactivePatternContributor : ConfiguredContributorBase
{
    private readonly ILogger<ReactivePatternContributor> _logger;
    private readonly ReactiveSignalTracker _tracker;

    // YAML-configurable thresholds
    private int MinErrorEventsForAnalysis => GetParam("min_error_events", 2);
    private double CompliancePrecisionThreshold => GetParam("compliance_precision_threshold", 0.15);
    private double ComplianceBotConfidence => GetParam("compliance_bot_confidence", 0.45);
    private double GeometricCvThreshold => GetParam("geometric_cv_threshold", 0.25);
    private double GeometricBotConfidence => GetParam("geometric_bot_confidence", 0.5);
    private int GeometricMinSteps => GetParam("geometric_min_steps", 3);
    private double PathPersistenceThreshold => GetParam("path_persistence_threshold", 0.6);
    private double PathPersistenceBotConfidence => GetParam("path_persistence_bot_confidence", 0.4);
    private int CoordinatedRetryMinSignatures => GetParam("coordinated_retry_min_signatures", 3);
    private double CoordinatedRetryBotConfidence => GetParam("coordinated_retry_bot_confidence", 0.35);
    private double RateAdaptedBotConfidence => GetParam("rate_adapted_bot_confidence", 0.3);

    // Known backoff base values (exponential=2, Fibonacci=1.618, linear=1.0)
    private static readonly (double Base, string Name)[] KnownBases =
    [
        (2.0, "exponential"),
        (1.618, "fibonacci"),
        (1.5, "mild_exponential"),
        (1.0, "linear")
    ];

    public ReactivePatternContributor(
        ILogger<ReactivePatternContributor> logger,
        IDetectorConfigProvider configProvider,
        ReactiveSignalTracker tracker) : base(configProvider)
    {
        _logger = logger;
        _tracker = tracker;
    }

    public override string Name => "ReactivePattern";
    public override int Priority => 32; // After SessionVector (30), before heuristic

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.PrimarySignature)
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        var history = _tracker.GetHistory(signature);
        var now = DateTimeOffset.UtcNow;

        // Always emit count so downstream consumers know whether we have data
        state.WriteSignal(SignalKeys.ReactiveErrorEventCount, history.Count);

        if (history.Count < MinErrorEventsForAnalysis)
        {
            contributions.Add(NeutralContribution("ReactivePattern", "No prior error events to analyze"));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        var lastEvent = history[^1];
        state.WriteSignal(SignalKeys.ReactivePost4xxGapMs, (float)(now - lastEvent.ServedAt).TotalMilliseconds);

        AnalyzeRetryAfterCompliance(history, now, state, contributions);
        AnalyzePathPersistence(history, state, contributions);
        AnalyzeGeometricRetry(history, state, contributions);
        AnalyzeRateAdaptation(history, state, contributions);
        AnalyzeCoordinatedRetry(history, now, state, contributions);

        if (contributions.Count == 0)
            contributions.Add(NeutralContribution("ReactivePattern", "No reactive bot patterns detected"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void AnalyzeRetryAfterCompliance(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        DateTimeOffset now,
        BlackboardState state,
        List<DetectionContribution> contributions)
    {
        // Find the most recent 429 with a Retry-After header
        var lastThrottle = history
            .Where(e => e.StatusCode == 429 && e.RetryAfterSeconds.HasValue)
            .OrderByDescending(e => e.ServedAt)
            .FirstOrDefault();

        if (lastThrottle == default)
        {
            state.WriteSignal(SignalKeys.ReactiveRetryAfterCompliance, -1f);
            return;
        }

        var retryAfterMs = lastThrottle.RetryAfterSeconds!.Value * 1000.0;
        var actualGapMs = (now - lastThrottle.ServedAt).TotalMilliseconds;

        if (retryAfterMs <= 0)
        {
            state.WriteSignal(SignalKeys.ReactiveRetryAfterCompliance, -1f);
            return;
        }

        var compliance = actualGapMs / retryAfterMs;
        state.WriteSignal(SignalKeys.ReactiveRetryAfterCompliance, (float)compliance);

        // Inhuman precision: compliance ratio very close to 1.0 (within threshold)
        var deviation = Math.Abs(compliance - 1.0);
        if (deviation < CompliancePrecisionThreshold && compliance > 0.9)
        {
            _logger.LogDebug(
                "Retry-After compliance: ratio={Compliance:F3} (deviation={Dev:F3})",
                compliance, deviation);
            contributions.Add(BotContribution(
                "RetryCompliance",
                $"Retry-After compliance ratio {compliance:F2} (deviation {deviation:F2}) — inhuman timing precision",
                ComplianceBotConfidence));
        }
    }

    private void AnalyzePathPersistence(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        BlackboardState state,
        List<DetectionContribution> contributions)
    {
        var forbidden = history.Where(e => e.StatusCode == 403).ToList();
        if (forbidden.Count == 0)
        {
            state.WriteSignal(SignalKeys.ReactivePathPersistencePost403, 0f);
            state.WriteSignal(SignalKeys.ReactivePathPersistenceRatio, 0f);
            return;
        }

        var blockedPaths = forbidden.Select(e => e.Path).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentPath = state.HttpContext?.Request.Path.Value ?? "";
        var isPersisting = blockedPaths.Contains(currentPath);

        var persistenceRatio = history.Count(e => blockedPaths.Contains(e.Path)) / (double)history.Count;
        state.WriteSignal(SignalKeys.ReactivePathPersistencePost403, isPersisting ? 1f : 0f);
        state.WriteSignal(SignalKeys.ReactivePathPersistenceRatio, (float)persistenceRatio);

        if (isPersisting && persistenceRatio >= PathPersistenceThreshold)
        {
            contributions.Add(BotContribution(
                "PathPersistence",
                $"Path persistence post-403: retrying blocked path '{currentPath}' ({persistenceRatio:P0} of error events on blocked paths)",
                PathPersistenceBotConfidence));
        }
    }

    private void AnalyzeGeometricRetry(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        BlackboardState state,
        List<DetectionContribution> contributions)
    {
        if (history.Count < GeometricMinSteps + 1)
        {
            state.WriteSignal(SignalKeys.ReactiveGeometricRatioCv, -1f);
            state.WriteSignal(SignalKeys.ReactiveBackoffBase, 0f);
            state.WriteSignal(SignalKeys.ReactiveBackoffPattern, "none");
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
            state.WriteSignal(SignalKeys.ReactiveGeometricRatioCv, -1f);
            state.WriteSignal(SignalKeys.ReactiveBackoffBase, 0f);
            state.WriteSignal(SignalKeys.ReactiveBackoffPattern, "none");
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

        state.WriteSignal(SignalKeys.ReactiveGeometricRatioCv, (float)cv);
        state.WriteSignal(SignalKeys.ReactiveBackoffBase, (float)meanRatio);
        state.WriteSignal(SignalKeys.ReactiveBackoffPattern, distToClosest < 0.2 ? closestName : "unknown");

        if (cv < GeometricCvThreshold)
        {
            _logger.LogDebug(
                "Geometric retry pattern: mean ratio={Ratio:F2} ({Name}), CV={CV:F3}",
                meanRatio, closestName, cv);
            contributions.Add(BotContribution(
                "GeometricRetry",
                $"Geometric retry pattern: {closestName} backoff (ratio={meanRatio:F2}, CV={cv:F3})",
                GeometricBotConfidence));
        }
    }

    private void AnalyzeRateAdaptation(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        BlackboardState state,
        List<DetectionContribution> contributions)
    {
        // Rate-adapted bots slow down after 429s but KEEP retrying.
        // Humans stop. A monotonically increasing gap series between 429s = automated adaptation.
        var throttleEvents = history
            .Where(e => e.StatusCode == 429)
            .OrderBy(e => e.ServedAt)
            .ToList();

        if (throttleEvents.Count < 2)
        {
            state.WriteSignal(SignalKeys.ReactiveRateAdapted, 0f);
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

        state.WriteSignal(SignalKeys.ReactiveRateAdapted, adaptationScore);

        if (adaptationScore > 0.75f && throttleGaps.Count >= 2)
        {
            contributions.Add(BotContribution(
                "RateAdaptation",
                $"Rate adaptation after 429: gaps increasing ({adaptationScore:P0} monotone) — automated retry with backoff",
                RateAdaptedBotConfidence));
        }
    }

    private void AnalyzeCoordinatedRetry(
        IReadOnlyList<ReactiveSignalTracker.ErrorEvent> history,
        DateTimeOffset now,
        BlackboardState state,
        List<DetectionContribution> contributions)
    {
        if (history.Count == 0)
        {
            state.WriteSignal(SignalKeys.ReactiveCoordinatedRetry, 0f);
            state.WriteSignal(SignalKeys.ReactiveCoRetryerCount, 0);
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

        state.WriteSignal(SignalKeys.ReactiveCoordinatedRetry, maxCoRetriers > 0 ? 1f : 0f);
        state.WriteSignal(SignalKeys.ReactiveCoRetryerCount, maxCoRetriers);

        if (maxCoRetriers >= CoordinatedRetryMinSignatures)
        {
            contributions.Add(BotContribution(
                "CoordinatedRetry",
                $"Coordinated retry: {maxCoRetriers} signatures retrying same blocked paths simultaneously",
                CoordinatedRetryBotConfidence));
        }
    }
}
