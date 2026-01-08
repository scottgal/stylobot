using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Action to take for learned patterns.
/// </summary>
public enum LearnedPatternAction
{
    /// <summary>Log detection only, don't contribute to blocking</summary>
    LogOnly,

    /// <summary>Contribute to detection score but don't block alone</summary>
    ScoreOnly,

    /// <summary>Full detection behavior (can trigger blocking)</summary>
    Full
}

/// <summary>
///     A learned bot signature that can be fed back to fast-path detectors.
/// </summary>
public record LearnedSignature
{
    /// <summary>Unique identifier for this pattern</summary>
    public required string PatternId { get; init; }

    /// <summary>Type of signature: UserAgent, IP, Behavior, HeaderPattern</summary>
    public required string SignatureType { get; init; }

    /// <summary>The pattern value (regex for UA, CIDR for IP, etc.)</summary>
    public required string Pattern { get; init; }

    /// <summary>Confidence level from learning (0.0-1.0)</summary>
    public double Confidence { get; init; }

    /// <summary>Number of times this pattern was observed</summary>
    public int Occurrences { get; init; }

    /// <summary>When this pattern was first learned</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>When this pattern was last seen</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>Action to take when this pattern matches</summary>
    public LearnedPatternAction Action { get; init; } = LearnedPatternAction.ScoreOnly;

    /// <summary>Bot type classification</summary>
    public BotType? BotType { get; init; }

    /// <summary>Optional bot name</summary>
    public string? BotName { get; init; }

    /// <summary>Source that discovered this pattern</summary>
    public string? Source { get; init; }
}

/// <summary>
///     Drift sample comparing fast-path vs full-path results.
/// </summary>
public record DriftSample
{
    public required string UaHash { get; init; }
    public required string UserAgent { get; init; }
    public bool FastPathIsBot { get; init; }
    public double FastPathConfidence { get; init; }
    public bool FullPathIsBot { get; init; }
    public double FullPathConfidence { get; init; }
    public bool Disagrees => FastPathIsBot != FullPathIsBot;
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
///     Drift statistics for a UA pattern.
/// </summary>
public class DriftStats
{
    public required string UaHash { get; init; }
    public string? SampleUserAgent { get; init; }
    public int TotalSamples { get; init; }
    public int Disagreements { get; init; }
    public double DisagreementRate => TotalSamples > 0 ? (double)Disagreements / TotalSamples : 0;
    public DateTimeOffset OldestSample { get; init; }
    public DateTimeOffset NewestSample { get; init; }
}

/// <summary>
///     Handles drift detection between fast-path and full-path results.
///     Compares sampled fast-path decisions against full 8-layer analysis
///     to detect when UA-only classification is drifting from reality.
///     Also routes feedback to appropriate detectors based on signature type.
/// </summary>
public class DriftDetectionHandler : ILearningEventHandler
{
    private readonly ConcurrentDictionary<string, LearnedSignature> _learnedPatterns = new();
    private readonly ILearningEventBus _learningBus;
    private readonly ILogger<DriftDetectionHandler> _logger;
    private readonly FastPathOptions _options;

    // In-memory sample storage (could be backed by persistent store)
    private readonly ConcurrentDictionary<string, List<DriftSample>> _samples = new();

    public DriftDetectionHandler(
        ILogger<DriftDetectionHandler> logger,
        IOptions<BotDetectionOptions> options,
        ILearningEventBus learningBus)
    {
        _logger = logger;
        _options = options.Value.FastPath;
        _learningBus = learningBus;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.MinimalDetection,
        LearningEventType.FullAnalysisRequest,
        LearningEventType.FullDetection,
        LearningEventType.HighConfidenceDetection
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        switch (evt.Type)
        {
            case LearningEventType.MinimalDetection:
                // Track minimal detection for correlation
                TrackMinimalDetection(evt);
                break;

            case LearningEventType.FullDetection:
                // Compare against any pending fast-path samples
                await ProcessFullDetectionAsync(evt, ct);
                break;

            case LearningEventType.HighConfidenceDetection:
                // High confidence = potential learning opportunity
                await ProcessHighConfidenceAsync(evt, ct);
                break;
        }
    }

    /// <summary>
    ///     Track minimal (fast-path) detection for later correlation.
    /// </summary>
    private void TrackMinimalDetection(LearningEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Pattern))
            return;

        var sample = new DriftSample
        {
            UaHash = evt.Pattern,
            UserAgent = evt.Metadata?.TryGetValue("userAgent", out var ua) == true
                ? ua?.ToString() ?? ""
                : "",
            FastPathIsBot = evt.Label ?? false,
            FastPathConfidence = evt.Confidence ?? 0,
            FullPathIsBot = false, // Will be updated when full analysis completes
            FullPathConfidence = 0,
            Timestamp = evt.Timestamp
        };

        var samples = _samples.GetOrAdd(evt.Pattern, _ => new List<DriftSample>());
        lock (samples)
        {
            samples.Add(sample);

            // Trim old samples
            var cutoff = DateTimeOffset.UtcNow.AddHours(-_options.DriftWindowHours);
            samples.RemoveAll(s => s.Timestamp < cutoff);
        }
    }

    /// <summary>
    ///     Process full detection result and check for drift.
    /// </summary>
    private async Task ProcessFullDetectionAsync(LearningEvent evt, CancellationToken ct)
    {
        if (!_options.EnableDriftDetection || string.IsNullOrEmpty(evt.Pattern))
            return;

        var uaHash = evt.Pattern;

        if (!_samples.TryGetValue(uaHash, out var samples))
            return;

        // Find the corresponding fast-path sample
        DriftSample? matchingSample = null;
        lock (samples)
        {
            matchingSample = samples.FirstOrDefault(s =>
                !s.FullPathIsBot && // Not yet updated
                Math.Abs((s.Timestamp - evt.Timestamp).TotalSeconds) < 60); // Within 60s

            if (matchingSample != null)
            {
                // Update with full-path result
                var index = samples.IndexOf(matchingSample);
                samples[index] = matchingSample with
                {
                    FullPathIsBot = evt.Label ?? false,
                    FullPathConfidence = evt.Confidence ?? 0
                };
            }
        }

        // Check for drift
        await CheckDriftAsync(uaHash, samples, ct);
    }

    /// <summary>
    ///     Check if drift threshold is exceeded for this pattern.
    /// </summary>
    private async Task CheckDriftAsync(string uaHash, List<DriftSample> samples, CancellationToken ct)
    {
        DriftStats stats;
        lock (samples)
        {
            var completeSamples = samples
                .Where(s => s.FullPathConfidence > 0) // Only samples with full analysis
                .ToList();

            if (completeSamples.Count < _options.MinSamplesForDrift)
                return;

            stats = new DriftStats
            {
                UaHash = uaHash,
                SampleUserAgent = completeSamples.FirstOrDefault()?.UserAgent,
                TotalSamples = completeSamples.Count,
                Disagreements = completeSamples.Count(s => s.Disagrees),
                OldestSample = completeSamples.Min(s => s.Timestamp),
                NewestSample = completeSamples.Max(s => s.Timestamp)
            };
        }

        if (stats.DisagreementRate > _options.DriftThreshold)
        {
            _logger.LogWarning(
                "Fast-path drift detected for {UaHash}: {Rate:P2} disagreement ({Disagreements}/{Total})",
                uaHash, stats.DisagreementRate, stats.Disagreements, stats.TotalSamples);

            // Emit drift event
            _learningBus.TryPublish(new LearningEvent
            {
                Type = LearningEventType.FastPathDriftDetected,
                Source = nameof(DriftDetectionHandler),
                Pattern = uaHash,
                Confidence = 1.0 - stats.DisagreementRate, // Trust level
                Metadata = new Dictionary<string, object>
                {
                    ["uaPattern"] = uaHash,
                    ["sampleUserAgent"] = stats.SampleUserAgent ?? "",
                    ["disagreementRate"] = stats.DisagreementRate,
                    ["totalSamples"] = stats.TotalSamples,
                    ["disagreements"] = stats.Disagreements,
                    ["recommendedAction"] = stats.DisagreementRate > 0.1
                        ? "remove_from_fast_path"
                        : "lower_confidence_weight"
                }
            });
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Process high-confidence detection for learning.
    /// </summary>
    private async Task ProcessHighConfidenceAsync(LearningEvent evt, CancellationToken ct)
    {
        if (!_options.EnableFeedbackLoop)
            return;

        if (evt.Confidence < _options.FeedbackMinConfidence)
            return;

        // Determine if this was a bot or human detection
        // Use botProbability from metadata if available, otherwise fall back to Label
        var wasBot = evt.Label ?? false;
        if (evt.Metadata?.TryGetValue("botProbability", out var probObj) == true && probObj is double botProb)
            wasBot = botProb >= 0.5;

        // Extract signature candidates
        var signatures = ExtractSignatures(evt);

        foreach (var sig in signatures)
            if (_learnedPatterns.TryGetValue(sig.PatternId, out var existing))
            {
                // Update occurrence count
                _learnedPatterns[sig.PatternId] = existing with
                {
                    Occurrences = existing.Occurrences + 1,
                    LastSeen = DateTimeOffset.UtcNow,
                    Confidence = Math.Max(existing.Confidence, sig.Confidence)
                };

                // Check if we should feed back
                if (existing.Occurrences + 1 >= _options.FeedbackMinOccurrences)
                    await EmitSignatureFeedbackAsync(sig, wasBot, ct);
            }
            else
            {
                // New pattern
                _learnedPatterns[sig.PatternId] = sig;

                _logger.LogDebug(
                    "New pattern learned: {Type} = {Pattern} (confidence={Confidence:F2}, wasBot={WasBot})",
                    sig.SignatureType, sig.Pattern, sig.Confidence, wasBot);
            }
    }

    /// <summary>
    ///     Extract signature candidates from a high-confidence detection.
    /// </summary>
    private List<LearnedSignature> ExtractSignatures(LearningEvent evt)
    {
        var signatures = new List<LearnedSignature>();

        // User-Agent signature
        if (evt.Metadata?.TryGetValue("userAgent", out var uaObj) == true &&
            uaObj is string ua && !string.IsNullOrEmpty(ua))
            signatures.Add(new LearnedSignature
            {
                PatternId = $"ua:{evt.Pattern}",
                SignatureType = "UserAgent",
                Pattern = ua,
                Confidence = evt.Confidence ?? 0,
                Occurrences = 1,
                FirstSeen = evt.Timestamp,
                LastSeen = evt.Timestamp,
                Action = LearnedPatternAction.LogOnly, // Start conservative
                BotType = ParseBotType(evt.Metadata),
                BotName = evt.Metadata?.TryGetValue("botName", out var name) == true
                    ? name?.ToString()
                    : null,
                Source = evt.Source
            });

        // IP signature
        if (evt.Metadata?.TryGetValue("ip", out var ipObj) == true &&
            ipObj is string ip && !string.IsNullOrEmpty(ip) && ip != "unknown")
            signatures.Add(new LearnedSignature
            {
                PatternId = $"ip:{ip}",
                SignatureType = "IP",
                Pattern = ip,
                Confidence = evt.Confidence ?? 0,
                Occurrences = 1,
                FirstSeen = evt.Timestamp,
                LastSeen = evt.Timestamp,
                Action = LearnedPatternAction.LogOnly,
                Source = evt.Source
            });

        return signatures;
    }

    /// <summary>
    ///     Emit feedback event to update fast-path detectors.
    /// </summary>
    private async Task EmitSignatureFeedbackAsync(LearnedSignature sig, bool wasBot, CancellationToken ct)
    {
        _logger.LogInformation(
            "Feeding back learned pattern: {Type} = {Pattern} (occurrences={Count}, action={Action}, wasBot={WasBot})",
            sig.SignatureType, sig.Pattern, sig.Occurrences, sig.Action, wasBot);

        _learningBus.TryPublish(new LearningEvent
        {
            Type = LearningEventType.SignatureFeedback,
            Source = nameof(DriftDetectionHandler),
            Pattern = sig.Pattern,
            Confidence = sig.Confidence,
            Label = wasBot, // Include bot/human classification
            Metadata = new Dictionary<string, object>
            {
                ["patternId"] = sig.PatternId,
                ["signatureType"] = sig.SignatureType,
                ["occurrences"] = sig.Occurrences,
                ["action"] = sig.Action.ToString(),
                ["botType"] = sig.BotType?.ToString() ?? "Unknown",
                ["botName"] = sig.BotName ?? "",
                ["firstSeen"] = sig.FirstSeen,
                ["lastSeen"] = sig.LastSeen,
                ["wasBot"] = wasBot
            }
        });

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Get all learned patterns for a specific detector type.
    /// </summary>
    public IEnumerable<LearnedSignature> GetLearnedPatterns(string signatureType)
    {
        return _learnedPatterns.Values
            .Where(p => p.SignatureType == signatureType)
            .Where(p => p.Occurrences >= _options.FeedbackMinOccurrences);
    }

    /// <summary>
    ///     Get drift statistics for all tracked patterns.
    /// </summary>
    public IEnumerable<DriftStats> GetDriftStats()
    {
        foreach (var (uaHash, samples) in _samples)
            lock (samples)
            {
                var completeSamples = samples
                    .Where(s => s.FullPathConfidence > 0)
                    .ToList();

                if (completeSamples.Count == 0)
                    continue;

                yield return new DriftStats
                {
                    UaHash = uaHash,
                    SampleUserAgent = completeSamples.FirstOrDefault()?.UserAgent,
                    TotalSamples = completeSamples.Count,
                    Disagreements = completeSamples.Count(s => s.Disagrees),
                    OldestSample = completeSamples.Min(s => s.Timestamp),
                    NewestSample = completeSamples.Max(s => s.Timestamp)
                };
            }
    }

    private static BotType? ParseBotType(Dictionary<string, object>? metadata)
    {
        if (metadata?.TryGetValue("botType", out var bt) != true)
            return null;

        return bt?.ToString() switch
        {
            "Scraper" => BotType.Scraper,
            "SearchEngine" => BotType.SearchEngine,
            "Monitor" => BotType.MonitoringBot,
            "MaliciousBot" => BotType.MaliciousBot,
            "VerifiedBot" => BotType.VerifiedBot,
            _ => BotType.Unknown
        };
    }
}