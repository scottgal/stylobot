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
        // Arrange - ConfirmedBad with score sitting close to the (lowered) DemoteFromBadScore.
        // Since commit d241863 ("oscillation fixes") the demote threshold dropped from 0.7
        // to 0.5 to widen the hysteresis gap with PromoteToBadScore (0.9). One round of
        // human evidence at alpha=0.1 takes 0.55 -> 0.495, which crosses the new threshold.
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.55,
            Support = 100, // Meets DemoteFromBadSupport (100)
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        // Act - apply human evidence to drop below threshold
        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        // Assert - score ~0.495 (below 0.5), should demote to Suspect
        Assert.Equal(ReputationState.Suspect, result.State);
    }

    [Fact]
    public void ApplyEvidence_ConfirmedBadInsideHysteresisGap_StaysConfirmedBad()
    {
        // Counterpart to the above: a single piece of human evidence applied to a
        // ConfirmedBad pattern that's still well above DemoteFromBadScore must NOT
        // flap the state. This is the actual oscillation-prevention contract.
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.72, // Above the 0.5 demote threshold even after one rebuttal
            Support = 100,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var result = _updater.ApplyEvidence(current, current.PatternId, current.PatternType, current.Pattern, 0.0);

        Assert.Equal(ReputationState.ConfirmedBad, result.State);
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
        // ConfirmedBad patterns use the longer tau pair (12h / 24h) introduced in commit
        // d241863 to prevent oscillation. After 3h the decay is much gentler than the
        // 3h/6h base tau would predict - that's the whole point.
        // ScoreDecayTauHours          = 3   (used by Neutral / Suspect)
        // ConfirmedBadScoreDecayTauHours = 12
        // SupportDecayTauHours        = 6
        // ConfirmedBadSupportDecayTauHours = 24
        // Confidence here = min(100/100, 1.0) = 1.0, so confidenceScale = 1.0 (no extra slowdown).
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.9,
            Support = 100,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastSeen = DateTimeOffset.UtcNow.AddHours(-3)
        };

        var result = _updater.ApplyTimeDecay(current);

        // Score decay: 0.9 + (0.5 - 0.9) * (1 - e^(-3/12)) = 0.9 - 0.4 * 0.221 ≈ 0.812
        Assert.InRange(result.BotScore, 0.79, 0.83);

        // Support decay: 100 * e^(-3/24) = 100 * 0.882 ≈ 88.2
        Assert.InRange(result.Support, 85, 92);
    }

    [Fact]
    public void ApplyTimeDecay_VeryStalePattern_NearPrior()
    {
        // After 12h on a ConfirmedBad pattern: score has done one ConfirmedBad-tau worth
        // of decay (12/12 = 1τ → ~63% of the way to prior), support half a tau (12/24 →
        // drops ~39%). Still nowhere near "near prior" - the test name predates the
        // ConfirmedBad-tau split and is kept for git history continuity even though the
        // assertion is now "decayed but still suspicious".
        var current = new PatternReputation
        {
            PatternId = "ua:test123",
            PatternType = "UserAgent",
            Pattern = "TestBot/1.0",
            BotScore = 0.95,
            Support = 500,
            State = ReputationState.ConfirmedBad,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-60),
            LastSeen = DateTimeOffset.UtcNow.AddHours(-12)
        };

        var result = _updater.ApplyTimeDecay(current);

        // Score: 0.95 + (0.5 - 0.95) * (1 - e^(-12/12)) = 0.95 - 0.45 * 0.632 ≈ 0.665
        Assert.InRange(result.BotScore, 0.64, 0.69);

        // Support: 500 * e^(-12/24) = 500 * 0.607 ≈ 303.5
        Assert.InRange(result.Support, 290, 315);
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