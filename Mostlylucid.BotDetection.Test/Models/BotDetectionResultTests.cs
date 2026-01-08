using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Models;

/// <summary>
///     Tests for BotDetectionResult model
/// </summary>
public class BotDetectionResultTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesReasonsList()
    {
        // Act
        var result = new BotDetectionResult();

        // Assert
        Assert.NotNull(result.Reasons);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Constructor_SetsDefaultConfidenceToZero()
    {
        // Act
        var result = new BotDetectionResult();

        // Assert
        Assert.Equal(0.0, result.ConfidenceScore);
    }

    [Fact]
    public void Constructor_SetsDefaultIsBotToFalse()
    {
        // Act
        var result = new BotDetectionResult();

        // Assert
        Assert.False(result.IsBot);
    }

    [Fact]
    public void Constructor_SetsDefaultBotTypeToNull()
    {
        // Act
        var result = new BotDetectionResult();

        // Assert
        Assert.Null(result.BotType);
    }

    [Fact]
    public void Constructor_SetsDefaultBotNameToNull()
    {
        // Act
        var result = new BotDetectionResult();

        // Assert
        Assert.Null(result.BotName);
    }

    [Fact]
    public void Constructor_SetsDefaultProcessingTimeToZero()
    {
        // Act
        var result = new BotDetectionResult();

        // Assert
        Assert.Equal(0, result.ProcessingTimeMs);
    }

    #endregion

    #region Property Setting Tests

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.7)]
    [InlineData(1.0)]
    public void ConfidenceScore_CanBeSet(double score)
    {
        // Arrange
        var result = new BotDetectionResult();

        // Act
        result.ConfidenceScore = score;

        // Assert
        Assert.Equal(score, result.ConfidenceScore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsBot_CanBeSet(bool isBot)
    {
        // Arrange
        var result = new BotDetectionResult();

        // Act
        result.IsBot = isBot;

        // Assert
        Assert.Equal(isBot, result.IsBot);
    }

    [Theory]
    [InlineData(BotType.Unknown)]
    [InlineData(BotType.SearchEngine)]
    [InlineData(BotType.SocialMediaBot)]
    [InlineData(BotType.MonitoringBot)]
    [InlineData(BotType.Scraper)]
    [InlineData(BotType.MaliciousBot)]
    [InlineData(BotType.GoodBot)]
    [InlineData(BotType.VerifiedBot)]
    public void BotType_CanBeSet(BotType type)
    {
        // Arrange
        var result = new BotDetectionResult();

        // Act
        result.BotType = type;

        // Assert
        Assert.Equal(type, result.BotType);
    }

    [Fact]
    public void BotName_CanBeSet()
    {
        // Arrange
        var result = new BotDetectionResult();
        var botName = "Googlebot";

        // Act
        result.BotName = botName;

        // Assert
        Assert.Equal(botName, result.BotName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(5000)]
    public void ProcessingTimeMs_CanBeSet(long time)
    {
        // Arrange
        var result = new BotDetectionResult();

        // Act
        result.ProcessingTimeMs = time;

        // Assert
        Assert.Equal(time, result.ProcessingTimeMs);
    }

    #endregion

    #region Reasons Collection Tests

    [Fact]
    public void Reasons_CanAddItems()
    {
        // Arrange
        var result = new BotDetectionResult();
        var reason = new DetectionReason
        {
            Category = "Test",
            Detail = "Test Detail",
            ConfidenceImpact = 0.5
        };

        // Act
        result.Reasons.Add(reason);

        // Assert
        Assert.Single(result.Reasons);
        Assert.Equal("Test", result.Reasons[0].Category);
    }

    [Fact]
    public void Reasons_CanAddMultipleItems()
    {
        // Arrange
        var result = new BotDetectionResult();

        // Act
        result.Reasons.Add(new DetectionReason { Category = "A", Detail = "Detail A", ConfidenceImpact = 0.1 });
        result.Reasons.Add(new DetectionReason { Category = "B", Detail = "Detail B", ConfidenceImpact = 0.2 });
        result.Reasons.Add(new DetectionReason { Category = "C", Detail = "Detail C", ConfidenceImpact = 0.3 });

        // Assert
        Assert.Equal(3, result.Reasons.Count);
    }

    [Fact]
    public void Reasons_CanBeCleared()
    {
        // Arrange
        var result = new BotDetectionResult();
        result.Reasons.Add(new DetectionReason { Category = "Test", Detail = "Detail", ConfidenceImpact = 0.1 });

        // Act
        result.Reasons.Clear();

        // Assert
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Reasons_CanBeReplaced()
    {
        // Arrange
        var result = new BotDetectionResult();
        var newReasons = new List<DetectionReason>
        {
            new() { Category = "New", Detail = "New Detail", ConfidenceImpact = 0.5 }
        };

        // Act
        result.Reasons = newReasons;

        // Assert
        Assert.Single(result.Reasons);
        Assert.Equal("New", result.Reasons[0].Category);
    }

    #endregion

    #region Complete Result Tests

    [Fact]
    public void CompleteResult_BotDetected()
    {
        // Arrange & Act
        var result = new BotDetectionResult
        {
            ConfidenceScore = 0.85,
            IsBot = true,
            BotType = BotType.Scraper,
            BotName = "Unknown Scraper",
            ProcessingTimeMs = 15,
            Reasons = new List<DetectionReason>
            {
                new() { Category = "User-Agent", Detail = "Automation framework detected", ConfidenceImpact = 0.5 },
                new() { Category = "Headers", Detail = "Missing Accept-Language", ConfidenceImpact = 0.2 }
            }
        };

        // Assert
        Assert.True(result.IsBot);
        Assert.True(result.ConfidenceScore > 0.7);
        Assert.Equal(2, result.Reasons.Count);
        Assert.Equal(BotType.Scraper, result.BotType);
    }

    [Fact]
    public void CompleteResult_HumanVisitor()
    {
        // Arrange & Act
        var result = new BotDetectionResult
        {
            ConfidenceScore = 0.1,
            IsBot = false,
            BotType = null,
            BotName = null,
            ProcessingTimeMs = 5,
            Reasons = new List<DetectionReason>()
        };

        // Assert
        Assert.False(result.IsBot);
        Assert.True(result.ConfidenceScore < 0.7);
        Assert.Empty(result.Reasons);
        Assert.Null(result.BotType);
    }

    [Fact]
    public void CompleteResult_VerifiedBot()
    {
        // Arrange & Act
        var result = new BotDetectionResult
        {
            ConfidenceScore = 0.0,
            IsBot = true, // It's a bot, but verified/trusted
            BotType = BotType.VerifiedBot,
            BotName = "Googlebot",
            ProcessingTimeMs = 3,
            Reasons = new List<DetectionReason>
            {
                new() { Category = "User-Agent", Detail = "Known verified bot: Googlebot", ConfidenceImpact = -1.0 }
            }
        };

        // Assert
        Assert.True(result.IsBot);
        Assert.Equal(0.0, result.ConfidenceScore);
        Assert.Equal(BotType.VerifiedBot, result.BotType);
        Assert.Equal("Googlebot", result.BotName);
    }

    #endregion
}