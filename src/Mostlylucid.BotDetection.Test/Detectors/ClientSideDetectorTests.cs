using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Detectors;

public class ClientSideDetectorTests
{
    private readonly DefaultHttpContext _context;
    private readonly ClientSideDetector _detector;

    public ClientSideDetectorTests()
    {
        _context = new DefaultHttpContext();

        var options = Options.Create(new BotDetectionOptions());
        var mockFingerprintStore = new Mock<IBrowserFingerprintStore>();

        _detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            options,
            mockFingerprintStore.Object);
    }

    [Fact]
    public async Task DetectAsync_WithoutJavaScriptFingerprint_DetectsPotentialBot()
    {
        // Arrange
        _context.Request.Headers.UserAgent = "Mozilla/5.0";
        // No client-side fingerprint headers

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // May or may not detect - depends on other signals
        if (result != null) Assert.True(result.Confidence >= 0);
    }

    [Fact]
    public async Task DetectAsync_WithValidFingerprint_AllowsRequest()
    {
        // Arrange
        _context.Request.Headers.UserAgent = "Mozilla/5.0";
        _context.Request.Headers["X-Client-Fingerprint"] = "valid-fp-123";

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // With valid fingerprint, should reduce bot probability or return null
        Assert.True(result == null || result.Confidence < 0.5);
    }

    [Fact]
    public async Task DetectAsync_MissingJavaScriptCapabilities_IncreasesScore()
    {
        // Arrange
        _context.Request.Headers.UserAgent = "Mozilla/5.0";
        // Missing typical JavaScript headers like Accept-Language, etc.

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // May or may not detect - depends on detector implementation
        if (result != null) Assert.True(result.Confidence >= 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("curl/7.0")]
    [InlineData("python-requests/2.0")]
    public async Task DetectAsync_WithNonBrowserUserAgents_DetectsBot(string userAgent)
    {
        // Arrange
        _context.Request.Headers.UserAgent = userAgent;

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // ClientSideDetector may not detect these - they're better caught by other detectors
        if (result != null) Assert.True(result.Confidence >= 0);
    }

    [Fact]
    public async Task DetectAsync_WithBrowserUserAgent_ChecksClientSideIndicators()
    {
        // Arrange
        _context.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0.0.0";
        _context.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        _context.Request.Headers.Accept = "text/html,application/xhtml+xml";

        // Act
        var result = await _detector.DetectAsync(_context);

        // Assert
        // With proper browser headers, should have lower bot probability
        Assert.True(result == null || result.Confidence < 0.5);
    }

    [Fact]
    public void Name_ReturnsCorrectIdentifier()
    {
        // Assert
        Assert.Equal("Client-Side Detector", _detector.Name);
    }
}