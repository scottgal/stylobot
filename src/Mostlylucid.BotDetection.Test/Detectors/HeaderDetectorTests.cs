using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Helpers;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Comprehensive tests for HeaderDetector
/// </summary>
public class HeaderDetectorTests
{
    private readonly HeaderDetector _detector;

    public HeaderDetectorTests()
    {
        var logger = new Mock<ILogger<HeaderDetector>>().Object;
        var options = Options.Create(new BotDetectionOptions());
        _detector = new HeaderDetector(logger, options);
    }

    #region Header Ordering Tests

    [Fact]
    public async Task DetectAsync_UserAgentLateInHeaders_AddsOrderingReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept"] = "text/html",
            ["Accept-Encoding"] = "gzip",
            ["Accept-Language"] = "en-US",
            ["Cache-Control"] = "no-cache",
            ["Connection"] = "keep-alive",
            ["Host"] = "example.com",
            ["User-Agent"] = "Mozilla/5.0" // Position 7 (index 6)
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("header ordering", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task DetectAsync_WithCancellation_CompletesNormally()
    {
        // Arrange
        var context = MockHttpContext.CreateRealisticBrowser();
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _detector.DetectAsync(context, cts.Token);

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Missing Headers Tests

    [Fact]
    public async Task DetectAsync_MissingAllCommonHeaders_ReturnsHighConfidence()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>());

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.5, "Missing all common headers should have high bot confidence");
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public async Task DetectAsync_MissingAcceptLanguage_AddsReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 Chrome",
            ["Accept"] = "text/html"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Accept-Language", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_MissingMultipleHeaders_AccumulatesConfidence()
    {
        // Arrange
        var contextWithOneHeader = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept"] = "text/html",
            ["Accept-Language"] = "en-US",
            ["Accept-Encoding"] = "gzip"
        });

        var contextWithFewHeaders = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept"] = "text/html"
        });

        // Act
        var resultOne = await _detector.DetectAsync(contextWithOneHeader);
        var resultFew = await _detector.DetectAsync(contextWithFewHeaders);

        // Assert
        Assert.True(resultFew.Confidence >= resultOne.Confidence,
            "More missing headers should increase confidence");
    }

    #endregion

    #region Accept-Language Tests

    [Fact]
    public async Task DetectAsync_GenericAcceptLanguage_AddsSuspiciousReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept-Language"] = "*"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Suspicious Accept-Language", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ShortAcceptLanguage_AddsSuspiciousReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept-Language"] = "en"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Accept-Language", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ProperAcceptLanguage_NoAcceptLanguageReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept"] = "text/html",
            ["Accept-Language"] = "en-US,en;q=0.9,de;q=0.8",
            ["Accept-Encoding"] = "gzip, deflate",
            ["Cache-Control"] = "no-cache",
            ["Connection"] = "keep-alive"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("Suspicious Accept-Language", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Accept Header Tests

    [Fact]
    public async Task DetectAsync_GenericAcceptWithNoLanguage_AddsReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept"] = "*/*"
            // No Accept-Language
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Generic Accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_GenericAcceptWithLanguage_NoGenericAcceptReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Accept"] = "*/*",
            ["Accept-Language"] = "en-US,en;q=0.9"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("Generic Accept header with no Accept-Language", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Connection Header Tests

    [Fact]
    public async Task DetectAsync_ConnectionCloseWithoutLanguage_AddsReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Connection"] = "close"
            // No Accept-Language
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Connection: close", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ConnectionKeepAlive_NoConnectionReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Connection"] = "keep-alive",
            ["Accept-Language"] = "en-US"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("Connection:", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Automation Headers Tests

    [Theory]
    [InlineData("X-Requested-With")]
    [InlineData("X-Automation")]
    [InlineData("X-Bot")]
    public async Task DetectAsync_AutomationHeader_ReturnsHighConfidence(string headerName)
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            [headerName] = "true",
            ["Accept-Language"] = "en-US"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3,
            $"Automation header ({headerName}) should increase confidence");
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains(headerName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_MultipleAutomationHeaders_AccumulatesConfidence()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Requested-With"] = "XMLHttpRequest",
            ["X-Automation"] = "true",
            ["X-Bot"] = "true"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Reasons.Count >= 3, "Each automation header should add a reason");
    }

    #endregion

    #region Few Headers Tests

    [Fact]
    public async Task DetectAsync_VeryFewHeaders_AddsReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Host"] = "example.com"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("few headers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ManyHeaders_NoFewHeadersReason()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["Host"] = "example.com",
            ["Accept"] = "text/html",
            ["Accept-Language"] = "en-US",
            ["Accept-Encoding"] = "gzip",
            ["Connection"] = "keep-alive"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("Very few headers", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Realistic Browser Tests

    [Fact]
    public async Task DetectAsync_RealisticBrowser_ReturnsLowConfidence()
    {
        // Arrange
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 0.4,
            "Realistic browser should have low bot confidence from headers");
    }

    [Fact]
    public async Task DetectAsync_RealisticBrowser_HasFewReasons()
    {
        // Arrange
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Reasons.Count <= 2,
            "Realistic browser should have few detection reasons from headers");
    }

    #endregion

    #region Confidence Bounds Tests

    [Fact]
    public async Task DetectAsync_ConfidenceNeverExceedsOne()
    {
        // Arrange - Many suspicious signals
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Automation"] = "true",
            ["X-Bot"] = "true",
            ["X-Requested-With"] = "test",
            ["Accept"] = "*/*",
            ["Connection"] = "close"
            // Missing many common headers
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 1.0, "Confidence should never exceed 1.0");
    }

    [Fact]
    public async Task DetectAsync_ConfidenceNeverNegative()
    {
        // Arrange
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.0, "Confidence should never be negative");
    }

    #endregion

    #region Reason Validation Tests

    [Fact]
    public async Task DetectAsync_AllReasonsHaveRequiredFields()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Bot"] = "true"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons)
        {
            Assert.False(string.IsNullOrEmpty(reason.Category), "Category should not be empty");
            Assert.False(string.IsNullOrEmpty(reason.Detail), "Detail should not be empty");
        }
    }

    [Fact]
    public async Task DetectAsync_AllReasonsHaveHeadersCategory()
    {
        // Arrange
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Bot"] = "true"
        });

        // Act
        var result = await _detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons) Assert.Equal("Headers", reason.Category);
    }

    #endregion
}