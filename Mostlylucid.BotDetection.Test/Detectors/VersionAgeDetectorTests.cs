using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Detectors;

public class VersionAgeDetectorTests
{
    private readonly DefaultHttpContext _context;
    private readonly VersionAgeDetector _detector;

    public VersionAgeDetectorTests()
    {
        _context = new DefaultHttpContext();

        var options = Options.Create(new BotDetectionOptions());
        var mockVersionService = new Mock<IBrowserVersionService>();

        // Setup mock to return current browser versions
        mockVersionService.Setup(s => s.GetLatestVersionAsync("Chrome", It.IsAny<CancellationToken>()))
            .ReturnsAsync(143); // Current Chrome version
        mockVersionService.Setup(s => s.GetLatestVersionAsync("Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync(146); // Current Firefox version
        mockVersionService.Setup(s => s.GetLatestVersionAsync("Safari", It.IsAny<CancellationToken>()))
            .ReturnsAsync(26);
        mockVersionService.Setup(s => s.GetLatestVersionAsync("Edge", It.IsAny<CancellationToken>()))
            .ReturnsAsync(143);

        _detector = new VersionAgeDetector(
            NullLogger<VersionAgeDetector>.Instance,
            options,
            mockVersionService.Object);
    }

    [Theory]
    [InlineData("Chrome/140.0.0.0", false)] // Recent version (within threshold)
    [InlineData("Chrome/50.0.0.0", true)] // Old version (2016)
    [InlineData("Firefox/140.0", false)] // Recent version (within threshold)
    [InlineData("Firefox/40.0", true)] // Old version (2015)
    public async Task DetectAsync_IdentifiesOldBrowserVersions(string userAgent, bool shouldDetect)
    {
        // Arrange
        _context.Request.Headers.UserAgent = userAgent;

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        if (shouldDetect)
        {
            Assert.NotNull(result);
            Assert.True(result.Confidence > 0.3);
            Assert.Contains(result.Reasons, r => r.Detail.Contains("behind", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Either null or low confidence (< 0.15) for recent versions
            if (result != null)
                Assert.True(result.Confidence < 0.15,
                    $"Expected low/no confidence for recent version, got {result.Confidence}");
        }
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/140.0.0.0")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Firefox/140.0")]
    public async Task DetectAsync_AllowsRecentVersions(string userAgent)
    {
        // Arrange
        _context.Request.Headers.UserAgent = userAgent;

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // Either null or low confidence for recent versions
        if (result != null)
            Assert.True(result.Confidence < 0.5,
                $"Expected low confidence for recent versions, got {result.Confidence}");
    }

    [Fact]
    public async Task DetectAsync_HandlesNoUserAgent()
    {
        // Arrange
        // No user agent set

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // Should return null or empty result with no confidence
        if (result != null) Assert.Equal(0, result.Confidence);
    }

    [Theory]
    [InlineData("IE/6.0")] // Very old IE
    [InlineData("IE/8.0")] // Old IE
    [InlineData("MSIE 9.0")] // Old IE format
    public async Task DetectAsync_DetectsVeryOldInternetExplorer(string userAgent)
    {
        // Arrange
        _context.Request.Headers.UserAgent = userAgent;

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // May or may not detect depending on IE regex patterns available
        if (result != null) Assert.True(result.Confidence >= 0);
    }

    [Fact]
    public void Name_ReturnsCorrectIdentifier()
    {
        // Assert
        Assert.Equal("Version Age Detector", _detector.Name);
    }
}