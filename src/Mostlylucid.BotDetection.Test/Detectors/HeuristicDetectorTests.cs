using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Helpers;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Tests for HeuristicDetector with learned weights
/// </summary>
public class HeuristicDetectorTests : IDisposable
{
    private readonly ILogger<HeuristicDetector> _logger;

    public HeuristicDetectorTests()
    {
        _logger = new Mock<ILogger<HeuristicDetector>>().Object;
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    private HeuristicDetector CreateDetector(BotDetectionOptions? options = null)
    {
        return new HeuristicDetector(
            _logger,
            Options.Create(options ?? new BotDetectionOptions()));
    }

    #region Category Tests

    [Fact]
    public async Task DetectAsync_Heuristic_UsesCorrectCategory()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.NotEmpty(result.Reasons);
        Assert.Equal("Heuristic", result.Reasons.First().Category);
    }

    #endregion

    #region Enabled Configuration Tests

    [Fact]
    public async Task DetectAsync_WhenDisabled_ReturnsEmptyResult()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = false }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(0, result.Confidence);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public async Task DetectAsync_WhenEnabled_ReturnsResult()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - should have a reason from heuristic model
        Assert.NotEmpty(result.Reasons);
    }

    #endregion

    #region Heuristic Tests

    [Fact]
    public async Task DetectAsync_HumanLikeRequest_ReturnsNegativeConfidenceImpact()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        // Create a human-like request with all the expected headers
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["User-Agent"] =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            ["Accept-Language"] = "en-US,en;q=0.5",
            ["Referer"] = "https://google.com/",
            ["Cookie"] = "session=abc123"
        });

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - human-like request should have negative confidence impact
        Assert.NotEmpty(result.Reasons);
        var reason = result.Reasons.First();
        Assert.Contains("human", reason.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(reason.ConfidenceImpact < 0, "Human-like request should have negative confidence impact");
    }

    [Fact]
    public async Task DetectAsync_BotLikeRequest_ReturnsPositiveConfidenceImpact()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        // Create a bot-like request with minimal headers and bot indicators
        var context = MockHttpContext.CreateWithUserAgent("bot/1.0 (+http://example.com/bot)");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - bot-like request should have positive confidence impact
        Assert.NotEmpty(result.Reasons);
        var reason = result.Reasons.First();
        Assert.Contains("bot", reason.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(reason.ConfidenceImpact > 0, "Bot-like request should have positive confidence impact");
    }

    [Theory]
    [InlineData("curl/7.64.1")]
    [InlineData("wget/1.20")]
    [InlineData("python-requests/2.25.1")]
    [InlineData("spider-bot/1.0")]
    [InlineData("crawler/2.0")]
    public async Task DetectAsync_KnownBotPatterns_DetectedAsBot(string userAgent)
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.NotEmpty(result.Reasons);
        var reason = result.Reasons.First();
        Assert.True(reason.ConfidenceImpact > 0, $"Bot pattern '{userAgent}' should be detected as bot");
    }

    #endregion

    #region Feature Name Tests

    [Fact]
    public async Task GetFeatureNames_AfterDetection_ReturnsDynamicFeatures()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act - Trigger initialization by running detection
        await detector.DetectAsync(context);
        var featureNames = detector.GetFeatureNames();

        // Assert - Should have default weights loaded
        Assert.True(featureNames.Count > 0, "Should have at least default feature names");
        Assert.Contains("ua:contains_bot", featureNames);
        Assert.Contains("hdr:accept-language", featureNames);
    }

    [Fact]
    public async Task GetCurrentWeights_AfterDetection_ReturnsDynamicWeights()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            AiDetection = new AiDetectionOptions
            {
                Heuristic = new HeuristicOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act - Trigger initialization by running detection
        await detector.DetectAsync(context);
        var weights = detector.GetCurrentWeights();

        // Assert - Should have default weights for known patterns
        Assert.True(weights.Count > 0, "Should have at least default weights");
        Assert.True(weights.ContainsKey("ua:contains_bot"), "Should have weight for bot UA pattern");
        Assert.True(weights["ua:contains_bot"] > 0, "Bot pattern should have positive weight");
    }

    #endregion
}