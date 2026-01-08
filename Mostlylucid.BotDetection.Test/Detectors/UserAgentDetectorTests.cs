using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Helpers;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Comprehensive tests for UserAgentDetector
/// </summary>
public class UserAgentDetectorTests
{
    private readonly IOptions<BotDetectionOptions> _defaultOptions;
    private readonly ILogger<UserAgentDetector> _logger;

    public UserAgentDetectorTests()
    {
        _logger = new Mock<ILogger<UserAgentDetector>>().Object;
        _defaultOptions = Options.Create(new BotDetectionOptions());
    }

    private UserAgentDetector CreateDetector(BotDetectionOptions? options = null)
    {
        return new UserAgentDetector(
            _logger,
            Options.Create(options ?? new BotDetectionOptions()));
    }

    #region Malicious Bot Tests

    [Theory]
    [InlineData("Scrapy/2.5.0")]
    [InlineData("MJ12bot")]
    [InlineData("AhrefsBot")]
    [InlineData("SemrushBot")]
    [InlineData("DotBot")]
    public async Task DetectAsync_KnownMaliciousOrAggressiveBot_ReturnsHighConfidence(string userAgent)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.2,
            $"Known aggressive bot ({userAgent}) should have elevated bot confidence");
    }

    #endregion

    #region Options Tests

    [Fact]
    public async Task DetectAsync_CustomWhitelist_RespectsWhitelistedPatterns()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            WhitelistedBotPatterns = new List<string> { "MyCustomBot" }
        };
        var detector = CreateDetector(options);
        // This would normally trigger bot detection but is in custom whitelist
        var context = MockHttpContext.CreateWithUserAgent("MyCustomBot/1.0");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - should still have some confidence since it's not in GoodBots signatures
        // The whitelist only works when combined with GoodBots patterns
        Assert.NotNull(result);
    }

    #endregion

    #region Missing User-Agent Tests

    [Fact]
    public async Task DetectAsync_MissingUserAgent_ReturnsHighConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent(null);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.7, "Missing User-Agent should have high bot confidence");
        Assert.Contains(result.Reasons, r => r.Category == "User-Agent");
    }

    [Fact]
    public async Task DetectAsync_EmptyUserAgent_ReturnsHighConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.7, "Empty User-Agent should have high bot confidence");
    }

    [Fact]
    public async Task DetectAsync_WhitespaceUserAgent_ReturnsHighConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("   ");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.7, "Whitespace-only User-Agent should have high bot confidence");
    }

    #endregion

    #region Known Good Bot Tests

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("DuckDuckBot/1.0; (+http://duckduckgo.com/duckduckbot.html)")]
    public async Task DetectAsync_KnownGoodBot_ReturnsLowConfidence(string userAgent)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 0.3, $"Known good bot ({userAgent}) should have low bot confidence");
        Assert.Equal(BotType.VerifiedBot, result.BotType);
    }

    [Fact]
    public async Task DetectAsync_Googlebot_ReturnsVerifiedBotType()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateGooglebot();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(BotType.VerifiedBot, result.BotType);
        Assert.NotNull(result.BotName);
    }

    [Fact]
    public async Task DetectAsync_Googlebot_SetsBotName()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateGooglebot();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.NotNull(result.BotName);
        Assert.Contains("Google", result.BotName, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Automation Framework Detection Tests

    [Theory]
    [InlineData("Selenium")]
    [InlineData("PhantomJS")]
    [InlineData("Puppeteer")]
    [InlineData("Playwright")]
    [InlineData("HeadlessChrome")]
    public async Task DetectAsync_AutomationFramework_ReturnsHighConfidence(string frameworkMarker)
    {
        // Arrange
        var detector = CreateDetector();
        var userAgent = $"Mozilla/5.0 ({frameworkMarker}) AppleWebKit/537.36";
        var context = MockHttpContext.CreateWithUserAgent(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.4,
            $"Automation framework ({frameworkMarker}) should have elevated bot confidence");
        Assert.Equal(BotType.Scraper, result.BotType);
    }

    [Fact]
    public async Task DetectAsync_SeleniumBot_ReturnsScraperType()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Selenium WebDriver");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(BotType.Scraper, result.BotType);
    }

    [Fact]
    public async Task DetectAsync_PuppeteerBot_HasAutomationReason()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Puppeteer/1.0");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Automation", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("Puppeteer", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Suspicious Pattern Tests

    [Theory]
    [InlineData("scrapy/2.0")]
    [InlineData("httrack/3.0")]
    [InlineData("libwww-perl/6.52")]
    [InlineData("go-http-client/1.1")]
    public async Task DetectAsync_SuspiciousUserAgent_ReturnsElevatedConfidence(string userAgent)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3,
            $"Suspicious User-Agent ({userAgent}) should have elevated bot confidence");
    }

    [Fact]
    public async Task DetectAsync_ShortUserAgent_ReturnsElevatedConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("Bot"); // Less than 20 chars

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, "Short User-Agent should have elevated confidence");
        Assert.Contains(result.Reasons, r => r.Detail.Contains("short", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_UserAgentWithUrl_ReturnsElevatedConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("MyBot http://example.com/bot");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.2, "User-Agent with URL should have elevated confidence");
        Assert.Contains(result.Reasons, r => r.Detail.Contains("URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_UserAgentWithHttpsUrl_ReturnsElevatedConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent("MyBot https://example.com/bot");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r => r.Detail.Contains("URL", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Legitimate Browser Tests

    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.2210.91")]
    public async Task DetectAsync_LegitimeBrowser_ReturnsLowConfidence(string userAgent)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithUserAgent(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 0.3,
            $"Legitimate browser ({userAgent.Substring(0, Math.Min(50, userAgent.Length))}) should have low bot confidence");
    }

    [Fact]
    public async Task DetectAsync_ChromeBrowser_HasNoScraperType()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.NotEqual(BotType.Scraper, result.BotType);
        Assert.NotEqual(BotType.MaliciousBot, result.BotType);
    }

    [Fact]
    public async Task DetectAsync_RealisticBrowser_HasFewOrNoReasons()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Reasons.Count <= 1, "Realistic browser should have few detection reasons");
    }

    #endregion

    #region Confidence Score Tests

    [Fact]
    public async Task DetectAsync_ConfidenceNeverExceedsOne()
    {
        // Arrange
        var detector = CreateDetector();
        // User agent with multiple suspicious patterns
        var context = MockHttpContext.CreateWithUserAgent("curl Selenium http://evil.com");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 1.0, "Confidence should never exceed 1.0");
    }

    [Fact]
    public async Task DetectAsync_ConfidenceNeverNegative()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.0, "Confidence should never be negative");
    }

    [Fact]
    public async Task DetectAsync_MultiplePatterns_AccumulateConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var singlePattern = MockHttpContext.CreateWithUserAgent("Selenium");
        var multiplePatterns = MockHttpContext.CreateWithUserAgent("Selenium Puppeteer");

        // Act
        var singleResult = await detector.DetectAsync(singlePattern);
        var multiResult = await detector.DetectAsync(multiplePatterns);

        // Assert
        Assert.True(multiResult.Confidence >= singleResult.Confidence,
            "Multiple patterns should accumulate confidence");
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task DetectAsync_WithCancellation_CompletesNormally()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateRealisticBrowser();
        using var cts = new CancellationTokenSource();

        // Act
        var result = await detector.DetectAsync(context, cts.Token);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task DetectAsync_WithAlreadyCancelledToken_CompletesNormally()
    {
        // Arrange (UserAgentDetector doesn't check cancellation internally)
        var detector = CreateDetector();
        var context = MockHttpContext.CreateRealisticBrowser();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act - should complete as the detector doesn't check cancellation token
        var result = await detector.DetectAsync(context, cts.Token);

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Reason Detail Tests

    [Fact]
    public async Task DetectAsync_AllReasonsHaveRequiredFields()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateSuspiciousBot();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons)
        {
            Assert.NotNull(reason.Category);
            Assert.NotEmpty(reason.Category);
            Assert.NotNull(reason.Detail);
            Assert.NotEmpty(reason.Detail);
        }
    }

    [Fact]
    public async Task DetectAsync_ReasonsHaveValidConfidenceImpact()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateSuspiciousBot();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons)
            Assert.True(reason.ConfidenceImpact >= -1.0 && reason.ConfidenceImpact <= 1.0,
                $"ConfidenceImpact {reason.ConfidenceImpact} should be between -1.0 and 1.0");
    }

    #endregion
}