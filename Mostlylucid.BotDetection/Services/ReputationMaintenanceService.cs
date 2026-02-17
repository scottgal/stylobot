using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that maintains pattern reputations:
///     - Periodic time decay sweep (pushes stale scores toward neutral)
///     - Garbage collection of old, neutral patterns
///     - Persistence to backing store
///     - Listens for feedback events to update reputations
/// </summary>
public class ReputationMaintenanceService : BackgroundService, ILearningEventHandler
{
    private readonly IPatternReputationCache _cache;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<ReputationMaintenanceService> _logger;
    private readonly ReputationOptions _options;
    private readonly PatternReputationUpdater _updater;

    public ReputationMaintenanceService(
        ILogger<ReputationMaintenanceService> logger,
        IPatternReputationCache cache,
        PatternReputationUpdater updater,
        IOptions<BotDetectionOptions> options,
        ILearningEventBus? learningBus = null)
    {
        _logger = logger;
        _cache = cache;
        _updater = updater;
        _options = options.Value.Reputation;
        _learningBus = learningBus;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.HighConfidenceDetection,
        LearningEventType.FullDetection,
        LearningEventType.MinimalDetection,
        LearningEventType.SignatureFeedback,
        LearningEventType.UserFeedback
    };

    /// <summary>
    ///     Handle learning events to update pattern reputations.
    /// </summary>
    public Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        try
        {
            switch (evt.Type)
            {
                case LearningEventType.HighConfidenceDetection:
                case LearningEventType.FullDetection:
                case LearningEventType.MinimalDetection:
                    HandleDetectionEvent(evt);
                    break;

                case LearningEventType.SignatureFeedback:
                    HandleSignatureFeedback(evt);
                    break;

                case LearningEventType.UserFeedback:
                    HandleUserFeedback(evt);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling learning event {Type}", evt.Type);
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reputation maintenance service starting");

        // Load persisted reputations on startup
        try
        {
            await _cache.LoadAsync(stoppingToken);
            var stats = _cache.GetStats();
            _logger.LogInformation(
                "Loaded {Count} pattern reputations ({Bad} bad, {Suspect} suspect, {Good} good)",
                stats.TotalPatterns, stats.ConfirmedBadCount, stats.SuspectCount, stats.ConfirmedGoodCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted reputations, starting fresh");
        }

        var decayInterval = TimeSpan.FromMinutes(_options.DecaySweepIntervalMinutes);
        var gcInterval = TimeSpan.FromHours(24); // GC once per day
        var persistInterval = TimeSpan.FromMinutes(5); // Persist every 5 minutes

        var lastDecay = DateTimeOffset.UtcNow;
        var lastGc = DateTimeOffset.UtcNow;
        var lastPersist = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

                var now = DateTimeOffset.UtcNow;

                // Decay sweep
                if (now - lastDecay >= decayInterval)
                {
                    await _cache.DecaySweepAsync(stoppingToken);
                    lastDecay = now;
                }

                // Garbage collection
                if (now - lastGc >= gcInterval)
                {
                    await _cache.GarbageCollectAsync(stoppingToken);
                    lastGc = now;

                    // Log stats after GC
                    var stats = _cache.GetStats();
                    _logger.LogInformation(
                        "Reputation stats: {Total} patterns, {Bad} bad, {Suspect} suspect, {GcEligible} GC-eligible",
                        stats.TotalPatterns, stats.ConfirmedBadCount, stats.SuspectCount, stats.GcEligibleCount);
                }

                // Persistence
                if (now - lastPersist >= persistInterval)
                {
                    await _cache.PersistAsync(stoppingToken);
                    lastPersist = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in reputation maintenance loop");
            }

        // Final persist on shutdown
        try
        {
            await _cache.PersistAsync(CancellationToken.None);
            _logger.LogInformation("Reputation maintenance service stopped, reputations persisted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist reputations on shutdown");
        }
    }

    private void HandleDetectionEvent(LearningEvent evt)
    {
        // Extract patterns from the detection event
        var patterns = ExtractPatternsFromEvent(evt);

        foreach (var (patternId, patternType, pattern) in patterns)
        {
            var current = _cache.Get(patternId);
            var label = evt.Label == true ? 1.0 : 0.0;
            var weight = evt.Confidence ?? 0.5;

            var updated = _updater.ApplyEvidence(current, patternId, patternType, pattern, label, weight);
            _cache.Update(updated);
        }
    }

    private void HandleSignatureFeedback(LearningEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Pattern))
            return;

        var patternId = evt.Metadata?.TryGetValue("patternId", out var id) == true
            ? id?.ToString()
            : evt.Pattern;

        if (string.IsNullOrEmpty(patternId))
            return;

        var signatureType = evt.Metadata?.TryGetValue("signatureType", out var st) == true
            ? st?.ToString()
            : "Unknown";

        var current = _cache.GetOrCreate(patternId, signatureType ?? "Unknown", evt.Pattern);

        // Signature feedback is typically high-confidence bot evidence
        var updated = _updater.ApplyEvidence(current, patternId, signatureType ?? "Unknown", evt.Pattern, 1.0,
            evt.Confidence ?? 0.9);
        _cache.Update(updated);

        _logger.LogDebug(
            "Updated reputation for {PatternId}: score={Score:F2}, state={State}",
            patternId, updated.BotScore, updated.State);
    }

    private void HandleUserFeedback(LearningEvent evt)
    {
        // User feedback is authoritative - apply with high weight
        var patterns = ExtractPatternsFromEvent(evt);
        var label = evt.Label == true ? 1.0 : 0.0;

        foreach (var (patternId, patternType, pattern) in patterns)
        {
            var current = _cache.GetOrCreate(patternId, patternType, pattern);

            // User feedback has high weight
            var updated = _updater.ApplyEvidence(current, patternId, patternType, pattern, label, 2.0);
            _cache.Update(updated);

            _logger.LogInformation(
                "User feedback applied to {PatternId}: label={Label}, new score={Score:F2}",
                patternId, label, updated.BotScore);
        }
    }

    private List<(string patternId, string patternType, string pattern)> ExtractPatternsFromEvent(LearningEvent evt)
    {
        var patterns = new List<(string, string, string)>();

        // User-Agent pattern — uses shared PatternNormalization to ensure
        // written keys match the keys read by FastPathReputation and ReputationBias.
        if (evt.Metadata?.TryGetValue("userAgent", out var uaObj) == true &&
            uaObj is string ua && !string.IsNullOrEmpty(ua))
        {
            var patternId = PatternNormalization.CreateUaPatternId(ua);
            patterns.Add((patternId, "UserAgent", ua));
        }

        // IP pattern — uses shared CIDR normalization to match contributor lookups.
        if (evt.Metadata?.TryGetValue("ip", out var ipObj) == true &&
            ipObj is string ip && !string.IsNullOrEmpty(ip) && ip != "unknown")
        {
            var patternId = PatternNormalization.CreateIpPatternId(ip);
            patterns.Add((patternId, "IP", ip));
        }

        // Explicit pattern from event
        if (!string.IsNullOrEmpty(evt.Pattern))
        {
            var type = evt.Metadata?.TryGetValue("signatureType", out var st) == true
                ? st?.ToString() ?? "Unknown"
                : "Unknown";
            patterns.Add((evt.Pattern, type, evt.Pattern));
        }

        return patterns;
    }
}