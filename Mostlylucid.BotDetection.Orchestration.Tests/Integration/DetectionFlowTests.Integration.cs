using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

[Trait("Category", "Integration")]
public class DetectionFlowTests
{
    private readonly InMemoryPatternReputationCache _cache;
    private readonly LearningEventBus _learningBus;
    private readonly BotDetectionOptions _options;
    private readonly PatternReputationUpdater _updater;

    public DetectionFlowTests()
    {
        _options = new BotDetectionOptions();
        var optionsWrapper = Options.Create(_options);

        _updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance,
            optionsWrapper);

        _cache = new InMemoryPatternReputationCache(
            NullLogger<InMemoryPatternReputationCache>.Instance,
            _updater);

        _learningBus = new LearningEventBus();
    }

    [Fact]
    public async Task NewBotPattern_LearnsAndPromotes()
    {
        // Simulate a new bot pattern being detected multiple times
        var patternId = "ua:BadBot123";
        var patternType = "UserAgent";
        var pattern = "BadBot/1.0 (scraper)";

        // Apply bot evidence 60 times (enough to reach ConfirmedBad)
        PatternReputation? current = null;
        for (var i = 0; i < 60; i++)
        {
            current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 1.0);
            _cache.Update(current);
        }

        var result = new
        {
            current!.PatternId,
            State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2),
            Support = Math.Round(current.Support, 0),
            current.CanTriggerFastAbort,
            FastPathWeight = Math.Round(current.FastPathWeight, 2)
        };

        await Verify(result);
    }

    [Fact]
    public async Task BotPatternRehabilitates_WhenHumanEvidenceAccumulates()
    {
        // Start with a confirmed bad pattern
        var patternId = "ua:RehabBot";
        var patternType = "UserAgent";
        var pattern = "RehabBot/1.0";

        // Build up bad reputation
        PatternReputation current = null!;
        for (var i = 0; i < 60; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 1.0);

        var beforeRehab = new
        {
            Phase = "Before Rehabilitation",
            State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2),
            Support = Math.Round(current.Support, 0)
        };

        // Now apply 150 human evidence events (need 100 support to demote from ConfirmedBad)
        for (var i = 0; i < 150; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 0.0);

        var afterRehab = new
        {
            Phase = "After Rehabilitation",
            State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2),
            Support = Math.Round(current.Support, 0)
        };

        await Verify(new { beforeRehab, afterRehab });
    }

    [Fact]
    public async Task TimeDecay_ForgetsStalePatterns()
    {
        // Create a pattern that was confirmed bad but hasn't been seen for 12 hours
        // ScoreDecayTauHours = 3, so 12/3 = 4 τ (nearly full decay)
        // SupportDecayTauHours = 6, so 12/6 = 2 τ (significant support loss)
        var current = new PatternReputation
        {
            PatternId = "ua:StaleBot",
            PatternType = "UserAgent",
            Pattern = "StaleBot/1.0",
            BotScore = 0.95,
            Support = 200,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-90),
            LastSeen = DateTimeOffset.UtcNow.AddHours(-12) // Not seen for 12 hours
        };

        var before = new
        {
            Phase = "Before Decay",
            State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2),
            Support = Math.Round(current.Support, 0)
        };

        var decayed = _updater.ApplyTimeDecay(current);

        var after = new
        {
            Phase = "After 12-hour Decay",
            State = decayed.State.ToString(),
            BotScore = Math.Round(decayed.BotScore, 2),
            Support = Math.Round(decayed.Support, 0)
        };

        await Verify(new { before, after });
    }

    [Fact]
    public async Task Hysteresis_PreventsFlapping()
    {
        // Demonstrate that once promoted to ConfirmedBad, it takes more evidence to demote
        var patternId = "ua:FlappyBot";
        var patternType = "UserAgent";
        var pattern = "FlappyBot/1.0";

        var states = new List<object>();

        // Build up to ConfirmedBad
        PatternReputation current = null!;
        for (var i = 0; i < 60; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 1.0);
        states.Add(new
        {
            Phase = "Built up to ConfirmedBad", State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2)
        });

        // Apply some human evidence (not enough to demote)
        for (var i = 0; i < 30; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 0.0);
        states.Add(new
        {
            Phase = "After 30 human events", State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2)
        });

        // Apply more human evidence (still not enough - need 100 support at low score)
        for (var i = 0; i < 40; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 0.0);
        states.Add(new
        {
            Phase = "After 70 human events", State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2)
        });

        // Now enough to demote
        for (var i = 0; i < 50; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 0.0);
        states.Add(new
        {
            Phase = "After 120 human events", State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2)
        });

        await Verify(states);
    }

    [Fact]
    public async Task ManualOverride_NotAffectedByEvidence()
    {
        // Create a manually blocked pattern
        var current = new PatternReputation
        {
            PatternId = "ua:ManualBlock",
            PatternType = "UserAgent",
            Pattern = "ManualBlock/1.0",
            BotScore = 0.5,
            Support = 10,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-10),
            LastSeen = DateTimeOffset.UtcNow
        };

        // Manually block
        var blocked = _updater.ManuallyBlock(current, "Admin blocked this");

        var afterBlock = new
        {
            Phase = "After Manual Block",
            State = blocked.State.ToString(),
            blocked.BotScore,
            blocked.IsManual,
            blocked.Notes
        };

        // Try to apply 100 human evidence events
        var afterEvidence = blocked;
        for (var i = 0; i < 100; i++)
            afterEvidence = _updater.ApplyEvidence(afterEvidence, blocked.PatternId, blocked.PatternType,
                blocked.Pattern, 0.0);

        var afterHumanEvidence = new
        {
            Phase = "After 100 Human Evidence Events",
            State = afterEvidence.State.ToString(),
            afterEvidence.BotScore,
            afterEvidence.IsManual,
            afterEvidence.Notes
        };

        // Try time decay
        var staleBlocked = blocked with { LastSeen = DateTimeOffset.UtcNow.AddDays(-60) };
        var afterDecay = _updater.ApplyTimeDecay(staleBlocked);

        var afterTimeDecay = new
        {
            Phase = "After 60-day Time Decay",
            State = afterDecay.State.ToString(),
            afterDecay.BotScore,
            afterDecay.IsManual
        };

        await Verify(new { afterBlock, afterHumanEvidence, afterTimeDecay });
    }

    [Fact]
    public async Task GoodClient_PromotesToConfirmedGood()
    {
        var patternId = "ua:GoodClient";
        var patternType = "UserAgent";
        var pattern = "GoodClient/1.0 (partner)";

        // Apply consistent human evidence
        PatternReputation current = null!;
        for (var i = 0; i < 110; i++) current = _updater.ApplyEvidence(current, patternId, patternType, pattern, 0.0);

        var result = new
        {
            current.PatternId,
            State = current.State.ToString(),
            BotScore = Math.Round(current.BotScore, 2),
            Support = Math.Round(current.Support, 0),
            FastPathWeight = Math.Round(current.FastPathWeight, 2) // Should be negative
        };

        await Verify(result);
    }

    [Fact]
    public async Task CacheStats_ReflectsPatternDistribution()
    {
        // Add various patterns to the cache
        var patterns = new[]
        {
            ("ua:bot1", "UserAgent", "Bot1/1.0", 1.0, 60),
            ("ua:bot2", "UserAgent", "Bot2/1.0", 1.0, 30),
            ("ua:human1", "UserAgent", "Human1/1.0", 0.0, 120),
            ("ip:1.2.3.4", "IP", "1.2.3.4", 1.0, 20),
            ("ua:suspect", "UserAgent", "Suspect/1.0", 1.0, 15)
        };

        foreach (var (id, type, pattern, label, count) in patterns)
        {
            PatternReputation? current = null;
            for (var i = 0; i < count; i++) current = _updater.ApplyEvidence(current, id, type, pattern, label);
            _cache.Update(current!);
        }

        var stats = _cache.GetStats();

        var result = new
        {
            stats.TotalPatterns,
            stats.ConfirmedBadCount,
            stats.SuspectCount,
            stats.NeutralCount,
            stats.ConfirmedGoodCount,
            AverageBotScore = Math.Round(stats.AverageBotScore, 2)
        };

        await Verify(result);
    }

    [Fact]
    public async Task DecaySweep_UpdatesAllStalePatterns()
    {
        // Add some stale patterns
        var stalePattern = new PatternReputation
        {
            PatternId = "ua:stale1",
            PatternType = "UserAgent",
            Pattern = "Stale1/1.0",
            BotScore = 0.9,
            Support = 100,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-60),
            LastSeen = DateTimeOffset.UtcNow.AddHours(-6) // 6 hours stale (1 τ for support)
        };
        _cache.Update(stalePattern);

        var freshPattern = new PatternReputation
        {
            PatternId = "ua:fresh1",
            PatternType = "UserAgent",
            Pattern = "Fresh1/1.0",
            BotScore = 0.9,
            Support = 100,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-7),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10) // Recently seen (no decay)
        };
        _cache.Update(freshPattern);

        // Run decay sweep
        await _cache.DecaySweepAsync();

        var staleAfter = _cache.Get("ua:stale1");
        var freshAfter = _cache.Get("ua:fresh1");

        var result = new
        {
            StalePattern = new
            {
                staleAfter!.PatternId,
                BotScore = Math.Round(staleAfter.BotScore, 2),
                Support = Math.Round(staleAfter.Support, 0),
                State = staleAfter.State.ToString()
            },
            FreshPattern = new
            {
                freshAfter!.PatternId,
                BotScore = Math.Round(freshAfter.BotScore, 2),
                Support = Math.Round(freshAfter.Support, 0),
                State = freshAfter.State.ToString()
            }
        };

        await Verify(result);
    }
}