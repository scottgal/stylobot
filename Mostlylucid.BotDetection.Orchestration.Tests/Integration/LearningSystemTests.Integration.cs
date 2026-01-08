using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Tests for the pattern reputation system.
///     Tests the learning and forgetting logic in isolation.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Learning")]
public class LearningSystemTests
{
    private readonly ITestOutputHelper _output;

    public LearningSystemTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PatternReputationUpdater_NewPattern_StartsWithEvidence()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions());
        var updater = new PatternReputationUpdater(logger, options);

        // Act
        var result = updater.ApplyEvidence(
            null,
            "ua:test123",
            "UserAgent",
            "TestBot/1.0",
            1.0); // bot

        // Assert
        _output.WriteLine(
            $"New pattern: Score={result.BotScore:F3}, Support={result.Support:F1}, State={result.State}");
        Assert.Equal(1.0, result.BotScore);
        Assert.Equal(1.0, result.Support);
        Assert.Equal(ReputationState.Neutral, result.State);
    }

    [Fact]
    public void PatternReputationUpdater_MultipleUpdates_UsesEMA()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions
        {
            Reputation = new ReputationOptions { LearningRate = 0.2 }
        });
        var updater = new PatternReputationUpdater(logger, options);

        // Act - Apply multiple bot signals
        PatternReputation? rep = null;
        for (var i = 0; i < 15; i++)
            rep = updater.ApplyEvidence(
                rep,
                "ua:test123",
                "UserAgent",
                "TestBot/1.0",
                1.0);

        // Assert
        _output.WriteLine(
            $"After 15 bot signals: Score={rep!.BotScore:F3}, Support={rep.Support:F1}, State={rep.State}");
        Assert.True(rep.BotScore > 0.9, $"Expected score > 0.9, got {rep.BotScore}");
        Assert.Equal(15.0, rep.Support);
        // Should be Suspect (score >= 0.6, support >= 10)
        Assert.Equal(ReputationState.Suspect, rep.State);
    }

    [Fact]
    public void PatternReputationUpdater_PromotesToConfirmedBad()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions
        {
            Reputation = new ReputationOptions
            {
                LearningRate = 0.2,
                PromoteToBadScore = 0.9,
                PromoteToBadSupport = 50
            }
        });
        var updater = new PatternReputationUpdater(logger, options);

        // Act - Apply enough bot signals to reach ConfirmedBad
        PatternReputation? rep = null;
        for (var i = 0; i < 60; i++)
            rep = updater.ApplyEvidence(
                rep,
                "ua:test123",
                "UserAgent",
                "TestBot/1.0",
                1.0);

        // Assert
        _output.WriteLine(
            $"After 60 bot signals: Score={rep!.BotScore:F3}, Support={rep.Support:F1}, State={rep.State}");
        Assert.True(rep.BotScore >= 0.9, $"Expected score >= 0.9, got {rep.BotScore}");
        Assert.True(rep.Support >= 50, $"Expected support >= 50, got {rep.Support}");
        Assert.Equal(ReputationState.ConfirmedBad, rep.State);
    }

    [Fact]
    public void PatternReputationUpdater_MixedSignals_ConvergesToRate()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions
        {
            Reputation = new ReputationOptions { LearningRate = 0.1 }
        });
        var updater = new PatternReputationUpdater(logger, options);

        // Act - 80% bot, 20% human
        PatternReputation? rep = null;
        for (var i = 0; i < 100; i++)
        {
            var label = i % 5 != 0 ? 1.0 : 0.0; // 80% bot
            rep = updater.ApplyEvidence(
                rep,
                "ua:test123",
                "UserAgent",
                "TestBot/1.0",
                label);
        }

        // Assert
        _output.WriteLine($"Mixed signals (80% bot): Score={rep!.BotScore:F3}, Support={rep.Support:F1}");
        // With EMA, should converge toward 0.8
        Assert.True(rep.BotScore > 0.7 && rep.BotScore < 0.9,
            $"Expected score near 0.8, got {rep.BotScore}");
    }

    [Fact]
    public void PatternReputationUpdater_TimeDecay_MovesTowardPrior()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions
        {
            Reputation = new ReputationOptions
            {
                ScoreDecayTauHours = 1, // Very fast decay for testing
                SupportDecayTauHours = 2,
                Prior = 0.5
            }
        });
        var updater = new PatternReputationUpdater(logger, options);

        // Create a pattern with high bot score, last seen 10 hours ago
        var oldPattern = new PatternReputation
        {
            PatternId = "ua:old",
            PatternType = "UserAgent",
            Pattern = "OldBot/1.0",
            BotScore = 0.95,
            Support = 50,
            State = ReputationState.Suspect,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddHours(-10) // 10 hours ago
        };

        // Act
        var decayed = updater.ApplyTimeDecay(oldPattern);

        // Assert
        _output.WriteLine($"Before decay: Score={oldPattern.BotScore:F3}, Support={oldPattern.Support:F1}");
        _output.WriteLine($"After decay:  Score={decayed.BotScore:F3}, Support={decayed.Support:F1}");

        Assert.True(decayed.BotScore < oldPattern.BotScore, "Score should decay toward prior");
        Assert.True(decayed.Support < oldPattern.Support, "Support should decay");
    }

    [Fact]
    public void PatternReputationUpdater_ManualOverride_NotAffectedByLearning()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions());
        var updater = new PatternReputationUpdater(logger, options);

        var manualPattern = new PatternReputation
        {
            PatternId = "ua:blocked",
            PatternType = "UserAgent",
            Pattern = "BlockedBot/1.0",
            BotScore = 1.0,
            Support = 100,
            State = ReputationState.ManuallyBlocked,
            IsManual = true
        };

        // Act - Try to apply human signals
        var result = updater.ApplyEvidence(
            manualPattern,
            manualPattern.PatternId,
            manualPattern.PatternType,
            manualPattern.Pattern,
            0.0); // human signal

        // Assert
        _output.WriteLine(
            $"Manual blocked pattern after human signal: Score={result.BotScore:F3}, State={result.State}");
        Assert.Equal(ReputationState.ManuallyBlocked, result.State);
        Assert.Equal(1.0, result.BotScore); // Unchanged
    }

    [Fact]
    public void PatternReputationUpdater_GarbageCollection_EligibilityCheck()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions
        {
            Reputation = new ReputationOptions
            {
                GcEligibleDays = 90,
                GcSupportThreshold = 1.0,
                GcOnlyNeutral = true
            }
        });
        var updater = new PatternReputationUpdater(logger, options);

        var oldNeutralPattern = new PatternReputation
        {
            PatternId = "ua:old-neutral",
            PatternType = "UserAgent",
            Pattern = "OldBot/1.0",
            BotScore = 0.5,
            Support = 0.5,
            State = ReputationState.Neutral,
            LastSeen = DateTimeOffset.UtcNow.AddDays(-100) // 100 days ago
        };

        var recentPattern = new PatternReputation
        {
            PatternId = "ua:recent",
            PatternType = "UserAgent",
            Pattern = "RecentBot/1.0",
            BotScore = 0.5,
            Support = 0.5,
            State = ReputationState.Neutral,
            LastSeen = DateTimeOffset.UtcNow.AddDays(-10) // 10 days ago
        };

        var oldSuspectPattern = new PatternReputation
        {
            PatternId = "ua:old-suspect",
            PatternType = "UserAgent",
            Pattern = "SuspectBot/1.0",
            BotScore = 0.8,
            Support = 0.5,
            State = ReputationState.Suspect,
            LastSeen = DateTimeOffset.UtcNow.AddDays(-100) // 100 days ago
        };

        // Assert
        Assert.True(updater.IsEligibleForGc(oldNeutralPattern), "Old neutral pattern should be GC eligible");
        Assert.False(updater.IsEligibleForGc(recentPattern), "Recent pattern should not be GC eligible");
        Assert.False(updater.IsEligibleForGc(oldSuspectPattern),
            "Old suspect pattern should not be GC eligible (GcOnlyNeutral)");

        _output.WriteLine("GC eligibility tests passed");
    }

    [Fact]
    public void InMemoryReputationCache_GetOrCreate_WorksCorrectly()
    {
        // Arrange
        var logger = new TestLogger<InMemoryPatternReputationCache>();
        var updaterLogger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions());
        var updater = new PatternReputationUpdater(updaterLogger, options);
        var cache = new InMemoryPatternReputationCache(logger, updater);

        // Act
        var rep1 = cache.GetOrCreate("ua:test", "UserAgent", "TestBot/1.0");
        var rep2 = cache.GetOrCreate("ua:test", "UserAgent", "TestBot/1.0");

        // Assert
        Assert.Equal(rep1.PatternId, rep2.PatternId);
        Assert.Equal(0.5, rep1.BotScore); // Neutral start
        _output.WriteLine($"Created pattern: {rep1.PatternId}, Score={rep1.BotScore}");
    }

    [Fact]
    public void InMemoryReputationCache_Update_PersistsChanges()
    {
        // Arrange
        var logger = new TestLogger<InMemoryPatternReputationCache>();
        var updaterLogger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions());
        var updater = new PatternReputationUpdater(updaterLogger, options);
        var cache = new InMemoryPatternReputationCache(logger, updater);

        // Act
        var rep = cache.GetOrCreate("ua:test", "UserAgent", "TestBot/1.0");
        var updated = rep with { BotScore = 0.9, State = ReputationState.Suspect };
        cache.Update(updated);

        var retrieved = cache.Get("ua:test");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(0.9, retrieved.BotScore);
        Assert.Equal(ReputationState.Suspect, retrieved.State);
        _output.WriteLine($"Updated and retrieved: Score={retrieved.BotScore}, State={retrieved.State}");
    }

    [Fact]
    public void InMemoryReputationCache_GetStats_ReturnsCorrectCounts()
    {
        // Arrange
        var logger = new TestLogger<InMemoryPatternReputationCache>();
        var updaterLogger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions());
        var updater = new PatternReputationUpdater(updaterLogger, options);
        var cache = new InMemoryPatternReputationCache(logger, updater);

        // Add patterns in various states
        cache.Update(new PatternReputation
        {
            PatternId = "ua:1", PatternType = "UserAgent", Pattern = "Bot1",
            State = ReputationState.Neutral, BotScore = 0.5, Support = 5
        });
        cache.Update(new PatternReputation
        {
            PatternId = "ua:2", PatternType = "UserAgent", Pattern = "Bot2",
            State = ReputationState.Suspect, BotScore = 0.7, Support = 20
        });
        cache.Update(new PatternReputation
        {
            PatternId = "ua:3", PatternType = "UserAgent", Pattern = "Bot3",
            State = ReputationState.ConfirmedBad, BotScore = 0.95, Support = 100
        });

        // Act
        var stats = cache.GetStats();

        // Assert
        Assert.Equal(3, stats.TotalPatterns);
        Assert.Equal(1, stats.NeutralCount);
        Assert.Equal(1, stats.SuspectCount);
        Assert.Equal(1, stats.ConfirmedBadCount);

        _output.WriteLine(
            $"Stats: Total={stats.TotalPatterns}, Neutral={stats.NeutralCount}, Suspect={stats.SuspectCount}, Bad={stats.ConfirmedBadCount}");
    }

    [Fact]
    public void PatternReputationUpdater_Rehabilitation_ReducesScore()
    {
        // Arrange
        var logger = new TestLogger<PatternReputationUpdater>();
        var options = Options.Create(new BotDetectionOptions
        {
            Reputation = new ReputationOptions
            {
                LearningRate = 0.1,
                DemoteFromBadScore = 0.7,
                DemoteFromBadSupport = 100
            }
        });
        var updater = new PatternReputationUpdater(logger, options);

        // Build up a bad reputation first
        PatternReputation? rep = null;
        for (var i = 0; i < 60; i++)
            rep = updater.ApplyEvidence(
                rep,
                "ua:rehab",
                "UserAgent",
                "RehabBot/1.0",
                1.0);

        _output.WriteLine($"After 60 bot signals: Score={rep!.BotScore:F3}, State={rep.State}");
        Assert.Equal(ReputationState.ConfirmedBad, rep.State);

        // Now rehabilitate with human signals
        for (var i = 0; i < 150; i++)
            rep = updater.ApplyEvidence(
                rep,
                "ua:rehab",
                "UserAgent",
                "RehabBot/1.0",
                0.0); // human signal

        // Assert
        _output.WriteLine($"After rehabilitation: Score={rep.BotScore:F3}, State={rep.State}");
        Assert.True(rep.BotScore < 0.7, $"Expected score < 0.7 after rehabilitation, got {rep.BotScore}");
        // Should have been demoted from ConfirmedBad
        Assert.NotEqual(ReputationState.ConfirmedBad, rep.State);
    }
}

/// <summary>
///     Simple test logger for unit tests
/// </summary>
public class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // No-op for tests
    }
}