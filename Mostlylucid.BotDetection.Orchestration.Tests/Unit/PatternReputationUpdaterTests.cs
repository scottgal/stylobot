using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

public class PatternReputationUpdaterTests
{
    private readonly BotDetectionOptions _options;
    private readonly PatternReputationUpdater _updater;

    public PatternReputationUpdaterTests()
    {
        _options = new BotDetectionOptions();
        var optionsWrapper = Options.Create(_options);
        _updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance,
            optionsWrapper);
    }

    #region ApplyEvidence Tests

    [Fact]
    public void ApplyEvidence_NewPattern_CreatesWithInitialScore()
    {
        // Act
        var result = _updater.ApplyEvidence(
            null,
            "ua:test123",
            "UserAgent",
            "TestBot/1.0",
            1.0);

        // Assert
        Assert.Equal("ua:test123", result.PatternId);
        Assert.Equal("UserAgent", result.PatternType);
        Assert.Equal("TestBot/1.0", result.Pattern);
        Assert.Equal(1.0, result.BotScore);
        Assert.Equal(1.0, result.Support);
        Assert.Equal(ReputationState.Neutral, result.State); // Needs more support to promote
    }

    [Fact]
    public void ApplyEvidence_ExistingPattern_UpdatesViaEma()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.5,
            Support = 10,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply bot evidence (label=1.0) with default learning rate 0.1
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 1.0);

        // Assert - EMA: new = (1-0.1)*0.5 + 0.1*1.0 = 0.55
        Assert.Equal(0.55, result.BotScore, 2);
        Assert.Equal(11, result.Support);
    }

    [Fact]
    public void ApplyEvidence_RepeatedBotEvidence_IncreasesScore()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.5,
            Support = 10,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply 20 bot evidence events
        var result = current;
        for (var i = 0; i < 20; i++)
            result = _updater.ApplyEvidence(result, result.PatternId, result.PatternType, result.Pattern, 1.0);

        // Assert - score should be much higher now
        Assert.True(result.BotScore > 0.8, $"Expected score > 0.8, got {result.BotScore}");
        Assert.Equal(30, result.Support);
    }

    [Fact]
    public void ApplyEvidence_HumanEvidence_DecreasesScore()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.8,
            Support = 50,
            State = ReputationState.Suspect,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-7),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply human evidence (label=0.0)
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        // Assert - EMA: new = (1-0.1)*0.8 + 0.1*0.0 = 0.72
        Assert.Equal(0.72, result.BotScore, 2);
    }

    [Fact]
    public void ApplyEvidence_ManualOverride_DoesNotChange()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:blocked",
            PatternType = "UserAgent",
            Pattern = "BadBot/1.0",
            BotScore = 1.0,
            Support = 100,
            State = ReputationState.ManuallyBlocked,
            IsManual = true,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - try to apply human evidence
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        // Assert - score unchanged, only LastSeen updated
        Assert.Equal(1.0, result.BotScore);
        Assert.Equal(ReputationState.ManuallyBlocked, result.State);
        Assert.True(result.IsManual);
    }

    [Fact]
    public void ApplyEvidence_SupportCapped_AtMaxSupport()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.9,
            Support = 995, // Near max
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply evidence multiple times
        var result = current;
        for (var i = 0; i < 20; i++)
            result = _updater.ApplyEvidence(result, result.PatternId, result.PatternType, result.Pattern, 1.0);

        // Assert - support capped at 1000
        Assert.Equal(1000, result.Support);
    }

    #endregion

    #region State Transition Tests

    [Fact]
    public void ApplyEvidence_HighScoreHighSupport_PromotesToSuspect()
    {
        // Arrange - start neutral with score just above threshold
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.58, // Just below 0.6 threshold
            Support = 9, // Just below 10 threshold
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - push over threshold with bot evidence
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 1.0);

        // Assert - should promote to Suspect (score ~0.62, support = 10)
        Assert.Equal(ReputationState.Suspect, result.State);
    }

    [Fact]
    public void ApplyEvidence_SuspectWithHighScore_PromotesToConfirmedBad()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.89,
            Support = 49,
            State = ReputationState.Suspect,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-7),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - push over threshold
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 1.0);

        // Assert - should promote to ConfirmedBad (score ~0.9, support = 50)
        Assert.Equal(ReputationState.ConfirmedBad, result.State);
    }

    [Fact]
    public void ApplyEvidence_ConfirmedBadWithLowScore_DemotesToSuspect()
    {
        // Arrange - ConfirmedBad with score dropped below demotion threshold
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.72, // Just above 0.7 demotion threshold
            Support = 100, // Meets demotion support requirement
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply human evidence to drop below threshold
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        // Assert - score ~0.65, should demote to Suspect
        Assert.Equal(ReputationState.Suspect, result.State);
    }

    [Fact]
    public void ApplyEvidence_SuspectWithLowScore_DemotesToNeutral()
    {
        // Arrange - start with score at 0.35, below the DemoteToNeutralScore of 0.4
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.35, // Below 0.4 demotion threshold
            Support = 20,
            State = ReputationState.Suspect,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-7),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply human evidence (pushes score lower and triggers state re-evaluation)
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        // Assert - score ~0.315, should demote to Neutral because below 0.4 threshold
        Assert.Equal(ReputationState.Neutral, result.State);
    }

    [Fact]
    public void ApplyEvidence_LowScoreHighSupport_PromotesToConfirmedGood()
    {
        // Arrange - score at 0.09 (below PromoteToGoodScore of 0.1), support at 99
        var current = new PatternReputation
        {
            PatternId = "ua:goodclient",
            PatternType = "UserAgent",
            Pattern = "GoodClient/1.0",
            BotScore = 0.09, // Below 0.1 threshold
            Support = 99, // Just below 100 threshold
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply human evidence (support becomes 100, score stays below threshold)
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        // Assert - score ~0.081, support = 100, should promote to ConfirmedGood
        Assert.Equal(ReputationState.ConfirmedGood, result.State);
    }

    #endregion

    #region Time Decay Tests

    [Fact]
    public void ApplyTimeDecay_RecentPattern_NoChange()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.9,
            Support = 100,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-7),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-30) // Recently seen
        };

        // Act
        var result = _updater.ApplyTimeDecay(current);

        // Assert - no significant change (less than 1 hour)
        Assert.Equal(current.BotScore, result.BotScore, 2);
        Assert.Equal(current.Support, result.Support, 1);
    }

    [Fact]
    public void ApplyTimeDecay_StalePattern_DecaysTowardPrior()
    {
        // Arrange - pattern not seen for 7 days (168 hours = 1 τ for score)
        // ScoreDecayTauHours = 168, SupportDecayTauHours = 336
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.9,
            Support = 100,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-7) // 7 days ago (1 τ for score)
        };

        // Act
        var result = _updater.ApplyTimeDecay(current);

        // Assert - score decay: 0.9 + (0.5 - 0.9) * (1 - e^(-168/168)) = 0.9 - 0.4 * 0.632 ≈ 0.647
        Assert.InRange(result.BotScore, 0.60, 0.70);

        // Support decay: 100 * e^(-168/336) = 100 * 0.607 ≈ 60.7
        Assert.InRange(result.Support, 55, 70);
    }

    [Fact]
    public void ApplyTimeDecay_VeryStalePattern_NearPrior()
    {
        // Arrange - pattern not seen for 30 days (720 hours)
        // ScoreDecayTauHours = 168, so 720/168 ≈ 4.3 τ for score
        // SupportDecayTauHours = 336, so 720/336 ≈ 2.1 τ for support
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.95,
            Support = 500,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-60),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-30) // 30 days ago
        };

        // Act
        var result = _updater.ApplyTimeDecay(current);

        // Assert - score: 0.95 + (0.5 - 0.95) * (1 - e^(-720/168)) ≈ 0.95 - 0.45 * 0.986 ≈ 0.506
        Assert.InRange(result.BotScore, 0.5, 0.55);

        // Support: 500 * e^(-720/336) ≈ 500 * 0.117 ≈ 58.5
        Assert.InRange(result.Support, 50, 70);
    }

    [Fact]
    public void ApplyTimeDecay_ManualOverride_NoChange()
    {
        // Arrange
        var current = new PatternReputation
        {
            PatternId = "ua:blocked",
            PatternType = "UserAgent",
            Pattern = "BadBot/1.0",
            BotScore = 1.0,
            Support = 100,
            State = ReputationState.ManuallyBlocked,
            IsManual = true,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-60),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-30) // Old
        };

        // Act
        var result = _updater.ApplyTimeDecay(current);

        // Assert - no change to manual overrides
        Assert.Equal(1.0, result.BotScore);
        Assert.Equal(100, result.Support);
    }

    #endregion

    #region Garbage Collection Tests

    [Fact]
    public void IsEligibleForGc_NewPattern_NotEligible()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:new",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.5,
            Support = 0.5,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow
        };

        Assert.False(_updater.IsEligibleForGc(pattern));
    }

    [Fact]
    public void IsEligibleForGc_OldNeutralLowSupport_Eligible()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:old",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.5,
            Support = 0.5, // Below threshold
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-120),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-100) // > 90 days
        };

        Assert.True(_updater.IsEligibleForGc(pattern));
    }

    [Fact]
    public void IsEligibleForGc_OldButSuspect_NotEligible()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:old",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.7,
            Support = 0.5,
            State = ReputationState.Suspect, // Not Neutral
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-120),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-100)
        };

        Assert.False(_updater.IsEligibleForGc(pattern));
    }

    [Fact]
    public void IsEligibleForGc_OldButHighSupport_NotEligible()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:old",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.5,
            Support = 10, // Above threshold
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-120),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-100)
        };

        Assert.False(_updater.IsEligibleForGc(pattern));
    }

    [Fact]
    public void IsEligibleForGc_ManualOverride_NeverEligible()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:manual",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.5,
            Support = 0.1,
            State = ReputationState.ManuallyBlocked,
            IsManual = true,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-365),
            LastSeen = DateTimeOffset.UtcNow.AddDays(-200)
        };

        Assert.False(_updater.IsEligibleForGc(pattern));
    }

    #endregion

    #region Manual Override Tests

    [Fact]
    public void ManuallyBlock_SetsCorrectState()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:test",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.3,
            Support = 10,
            State = ReputationState.Neutral
        };

        var result = _updater.ManuallyBlock(pattern, "Known scraper");

        Assert.Equal(ReputationState.ManuallyBlocked, result.State);
        Assert.Equal(1.0, result.BotScore);
        Assert.True(result.IsManual);
        Assert.Equal("Known scraper", result.Notes);
    }

    [Fact]
    public void ManuallyAllow_SetsCorrectState()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:test",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 0.8,
            Support = 50,
            State = ReputationState.Suspect
        };

        var result = _updater.ManuallyAllow(pattern, "Verified partner");

        Assert.Equal(ReputationState.ManuallyAllowed, result.State);
        Assert.Equal(0.0, result.BotScore);
        Assert.True(result.IsManual);
        Assert.Equal("Verified partner", result.Notes);
    }

    [Fact]
    public void RemoveManualOverride_ResetsToAutomatic()
    {
        var pattern = new PatternReputation
        {
            PatternId = "ua:test",
            PatternType = "UserAgent",
            Pattern = "Test/1.0",
            BotScore = 1.0,
            Support = 50,
            State = ReputationState.ManuallyBlocked,
            IsManual = true,
            Notes = "Was blocked"
        };

        var result = _updater.RemoveManualOverride(pattern);

        Assert.False(result.IsManual);
        Assert.Null(result.Notes);
        // State should be re-evaluated based on score/support
        Assert.NotEqual(ReputationState.ManuallyBlocked, result.State);
    }

    #endregion
}