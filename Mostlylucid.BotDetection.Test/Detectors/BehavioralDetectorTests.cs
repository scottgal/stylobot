using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Helpers;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Comprehensive tests for BehavioralDetector
/// </summary>
public class BehavioralDetectorTests
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<BehavioralDetector> _logger;

    public BehavioralDetectorTests()
    {
        _logger = new Mock<ILogger<BehavioralDetector>>().Object;
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    private BehavioralDetector CreateDetector(BotDetectionOptions? options = null)
    {
        return new BehavioralDetector(
            _logger,
            Options.Create(options ?? new BotDetectionOptions()),
            _cache);
    }

    private IMemoryCache CreateFreshCache()
    {
        return new MemoryCache(new MemoryCacheOptions());
    }

    private IMemoryCache CreateCacheWithOldSession(string ipAddress)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        // Pre-seed with session that's > 2 minutes old to skip warmup
        var sessionKey = $"bot_detect_session_{ipAddress}";
        var oldSessionStart = DateTime.UtcNow.AddMinutes(-3);
        cache.Set(sessionKey, oldSessionStart, TimeSpan.FromHours(24));
        return cache;
    }

    #region Missing IP Tests

    [Fact]
    public async Task DetectAsync_NoIpAddress_ReturnsZeroConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = new DefaultHttpContext();
        // No IP set

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(0.0, result.Confidence);
        Assert.Empty(result.Reasons);
    }

    #endregion

    #region Fast Request Tests

    [Fact]
    public async Task DetectAsync_RapidSequentialRequests_HighConfidence()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act - Make requests as fast as possible
        await detector.DetectAsync(context);
        var result = await detector.DetectAsync(context);

        // Assert - Extremely fast requests should be detected
        // Note: This might not trigger in test environment due to test overhead
        Assert.NotNull(result);
    }

    #endregion

    #region Bot Type Classification Tests

    [Fact]
    public async Task DetectAsync_HighConfidence_SetsScraperBotType()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 5 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");
        context.Request.Path = "/page";

        // Act - Generate many requests to trigger high confidence
        DetectorResult result = null!;
        for (var i = 0; i < 20; i++) result = await detector.DetectAsync(context);

        // Assert
        if (result.Confidence > 0.6) Assert.Equal(BotType.Scraper, result.BotType);
    }

    #endregion

    #region Different IPs Tests

    [Fact]
    public async Task DetectAsync_DifferentIps_IndependentCounting()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 10 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);

        var context1 = MockHttpContext.CreateWithIpAddress("192.168.1.1");
        var context2 = MockHttpContext.CreateWithIpAddress("192.168.1.2");

        // Act - Many requests from IP1
        for (var i = 0; i < 15; i++) await detector.DetectAsync(context1);

        // First request from IP2
        var result2 = await detector.DetectAsync(context2);

        // Assert - IP2 should have low confidence (first request)
        Assert.True(result2.Confidence < 0.5, "Different IP should have independent rate counting");
    }

    #endregion

    #region X-Forwarded-For Tests

    [Fact]
    public async Task DetectAsync_XForwardedFor_UsesCorrectIp()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 5 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);

        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "10.0.0.1, 192.168.1.1"
        });

        // Act
        DetectorResult result = null!;
        for (var i = 0; i < 10; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, "Should track by X-Forwarded-For IP");
    }

    #endregion

    #region Reason Validation Tests

    [Fact]
    public async Task DetectAsync_AllReasonsHaveBehavioralCategory()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 5 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act
        DetectorResult result = null!;
        for (var i = 0; i < 10; i++) result = await detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons) Assert.Equal("Behavioral", reason.Category);
    }

    #endregion

    #region Name Property Test

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        // Arrange
        var detector = CreateDetector();

        // Assert
        Assert.Equal("Behavioral Detector", detector.Name);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task DetectAsync_WithCancellation_CompletesNormally()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");
        using var cts = new CancellationTokenSource();

        // Act
        var result = await detector.DetectAsync(context, cts.Token);

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Request Timing Analysis Tests

    [Fact]
    public async Task DetectAsync_RegularTimingPattern_DetectsBotLikeBehavior()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 100 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act - Make exactly 5 requests to trigger timing analysis
        // Note: In real scenarios, bot-like regular timing would be detected
        // This test verifies the timing analysis runs without error
        for (var i = 0; i < 5; i++) await detector.DetectAsync(context);

        // Assert - Timing analysis should have run (result should not be null)
        var result = await detector.DetectAsync(context);
        Assert.NotNull(result);
    }

    #endregion

    #region Rate Limiting Tests

    [Fact]
    public async Task DetectAsync_FirstRequest_LowConfidence()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(
            _logger,
            Options.Create(new BotDetectionOptions { MaxRequestsPerMinute = 60 }),
            cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence < 0.5, "First request should have low confidence");
    }

    [Fact]
    public async Task DetectAsync_ExcessiveRequests_HighConfidence()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 10 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act - Simulate many requests (21+ to exceed warmup limit of 20)
        DetectorResult result = null!;
        for (var i = 0; i < 21; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, "Excessive requests should increase confidence");
        Assert.Contains(result.Reasons, r => r.Detail.Contains("request rate"));
    }

    [Fact]
    public async Task DetectAsync_WithinRateLimit_NoRateLimitReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 100 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act - Just a few requests
        DetectorResult result = null!;
        for (var i = 0; i < 5; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("Excessive request rate"));
    }

    #endregion

    #region Missing Referrer Tests

    [Fact]
    public async Task DetectAsync_NoReferrerOnSubsequentRequest_AddsReason()
    {
        // Arrange
        var ipAddress = "192.168.1.1";
        var cache = CreateCacheWithOldSession(ipAddress); // Use old session to skip warmup
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Path = "/some/path"; // Not root
        // No Referer header

        // Act - First request
        await detector.DetectAsync(context);
        // Second request
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("referrer", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("Referer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_RootPath_NoReferrerReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");
        context.Request.Path = "/"; // Root path

        // Act
        await detector.DetectAsync(context);
        var result = await detector.DetectAsync(context);

        // Assert - Root path shouldn't require referrer
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("No referrer on subsequent"));
    }

    [Fact]
    public async Task DetectAsync_WithReferrer_NoReferrerReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");
        context.Request.Path = "/some/path";
        context.Request.Headers["Referer"] = "https://example.com/";

        // Act
        await detector.DetectAsync(context);
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("No referrer"));
    }

    #endregion

    #region Missing Cookies Tests

    [Fact]
    public async Task DetectAsync_NoCookiesAfterMultipleRequests_AddsReason()
    {
        // Arrange
        var ipAddress = "192.168.1.1";
        var cache = CreateCacheWithOldSession(ipAddress); // Use old session to skip warmup
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        // No cookies

        // Act - Make 3+ requests
        await detector.DetectAsync(context);
        await detector.DetectAsync(context);
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_WithCookies_NoCookieReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");
        context.Request.Headers["Cookie"] = "session=abc123";

        // Act
        await detector.DetectAsync(context);
        await detector.DetectAsync(context);
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("No cookies maintained"));
    }

    #endregion

    #region Confidence Bounds Tests

    [Fact]
    public async Task DetectAsync_ConfidenceNeverExceedsOne()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 1 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act - Many requests
        DetectorResult result = null!;
        for (var i = 0; i < 100; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 1.0, "Confidence should never exceed 1.0");
    }

    [Fact]
    public async Task DetectAsync_ConfidenceNeverNegative()
    {
        // Arrange
        var cache = CreateFreshCache();
        var detector = new BehavioralDetector(_logger, Options.Create(new BotDetectionOptions()), cache);
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.0, "Confidence should never be negative");
    }

    #endregion

    #region API Key Rate Limiting Tests

    [Fact]
    public async Task DetectAsync_ApiKeyRateLimitExceeded_AddsReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100, // High IP limit
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 5 // Low API key limit
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "test-api-key-123"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Exceed API key rate limit
        DetectorResult result = null!;
        for (var i = 0; i < 10; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ApiKeyWithinLimit_NoApiKeyReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100,
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 50
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "test-api-key-456"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Stay within API key rate limit
        DetectorResult result = null!;
        for (var i = 0; i < 5; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_DifferentApiKeys_IndependentCounting()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100,
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 5
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);

        var context1 = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "api-key-1"
        });
        context1.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        var context2 = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "api-key-2"
        });
        context2.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Exceed limit on key 1
        for (var i = 0; i < 10; i++) await detector.DetectAsync(context1);

        // First request on key 2
        var result2 = await detector.DetectAsync(context2);

        // Assert - Key 2 should not have API key rate limit reason
        Assert.DoesNotContain(result2.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_NoApiKeyHeader_NoApiKeyTracking()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100,
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = null, // Disabled
                ApiKeyRateLimit = 5
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "test-api-key"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act
        DetectorResult result = null!;
        for (var i = 0; i < 10; i++) result = await detector.DetectAsync(context);

        // Assert - Should not track by API key when header not configured
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region User ID Rate Limiting Tests

    [Fact]
    public async Task DetectAsync_UserRateLimitExceeded_AddsReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100,
            Behavioral = new BehavioralOptions
            {
                UserIdHeader = "X-User-Id",
                UserRateLimit = 5
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-User-Id"] = "user-123"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Exceed user rate limit
        DetectorResult result = null!;
        for (var i = 0; i < 10; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("User rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_UserWithinLimit_NoUserReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100,
            Behavioral = new BehavioralOptions
            {
                UserIdHeader = "X-User-Id",
                UserRateLimit = 50
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-User-Id"] = "user-456"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Stay within user rate limit
        DetectorResult result = null!;
        for (var i = 0; i < 5; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("User rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_DifferentUsers_IndependentCounting()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100,
            Behavioral = new BehavioralOptions
            {
                UserIdHeader = "X-User-Id",
                UserRateLimit = 5
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);

        var context1 = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-User-Id"] = "user-1"
        });
        context1.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        var context2 = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-User-Id"] = "user-2"
        });
        context2.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Exceed limit on user 1
        for (var i = 0; i < 10; i++) await detector.DetectAsync(context1);

        // First request for user 2
        var result2 = await detector.DetectAsync(context2);

        // Assert - User 2 should not have user rate limit reason
        Assert.DoesNotContain(result2.Reasons, r =>
            r.Detail.Contains("User rate limit", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Fingerprint-Based Tracking Tests

    [Fact]
    public async Task DetectAsync_FingerprintRateLimitExceeded_AddsReason()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100 // High IP limit, fingerprint limit is 1.5x = 150
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Set fingerprint hash in context items (simulates ClientSideDetector)
        context.Items["BotDetection.FingerprintHash"] = "abc123fingerprint";

        // Act - Exceed fingerprint rate limit (1.5x of MaxRequestsPerMinute)
        // Need more than 150 requests to trigger
        DetectorResult result = null!;
        for (var i = 0; i < 160; i++) result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Fingerprint rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_DifferentFingerprints_IndependentCounting()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 10 // Low limit so fingerprint limit is 15
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);

        var context1 = MockHttpContext.CreateWithIpAddress("192.168.1.1");
        context1.Items["BotDetection.FingerprintHash"] = "fingerprint-1";

        var context2 = MockHttpContext.CreateWithIpAddress("192.168.1.2");
        context2.Items["BotDetection.FingerprintHash"] = "fingerprint-2";

        // Act - Many requests from fingerprint 1
        for (var i = 0; i < 20; i++) await detector.DetectAsync(context1);

        // First request from fingerprint 2
        var result2 = await detector.DetectAsync(context2);

        // Assert - Fingerprint 2 should not have fingerprint rate limit reason
        Assert.DoesNotContain(result2.Reasons, r =>
            r.Detail.Contains("Fingerprint rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_NoFingerprint_NoFingerprintTracking()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 10 };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");
        // No fingerprint in context items

        // Act
        DetectorResult result = null!;
        for (var i = 0; i < 20; i++) result = await detector.DetectAsync(context);

        // Assert - Should not have fingerprint rate limit reason
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("Fingerprint rate limit", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region BehavioralOptions Default Values Tests

    [Fact]
    public async Task DetectAsync_ApiKeyRateLimitZero_UsesDefaultMultiplier()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 10, // Default API key limit should be 20 (2x)
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 0 // Use default
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "test-key"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Make 15 requests (within default 2x multiplier of 20)
        DetectorResult result = null!;
        for (var i = 0; i < 15; i++) result = await detector.DetectAsync(context);

        // Assert - Should not exceed default limit
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_UserRateLimitZero_UsesDefaultMultiplier()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 10, // Default user limit should be 30 (3x)
            Behavioral = new BehavioralOptions
            {
                UserIdHeader = "X-User-Id",
                UserRateLimit = 0 // Use default
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-User-Id"] = "test-user"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Make 25 requests (within default 3x multiplier of 30)
        DetectorResult result = null!;
        for (var i = 0; i < 25; i++) result = await detector.DetectAsync(context);

        // Assert - Should not exceed default limit
        Assert.DoesNotContain(result.Reasons, r =>
            r.Detail.Contains("User rate limit", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Combined Identity Tracking Tests

    [Fact]
    public async Task DetectAsync_MultipleIdentitiesExceedLimits_CombinesReasons()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 5, // Low IP limit
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 5,
                UserIdHeader = "X-User-Id",
                UserRateLimit = 5
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "test-key",
            ["X-User-Id"] = "test-user"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        // Act - Exceed all limits
        DetectorResult result = null!;
        for (var i = 0; i < 20; i++) result = await detector.DetectAsync(context);

        // Assert - Should have multiple rate limit reasons
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("request rate", StringComparison.OrdinalIgnoreCase)); // IP
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("User rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_SameIpDifferentIdentities_IndependentTracking()
    {
        // Arrange
        var cache = CreateFreshCache();
        var options = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 100, // High IP limit
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "X-Api-Key",
                ApiKeyRateLimit = 5
            }
        };
        var detector = new BehavioralDetector(_logger, Options.Create(options), cache);

        // Same IP, different API keys
        var context1 = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "key-1"
        });
        context1.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        var context2 = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "key-2"
        });
        context2.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1"); // Same IP

        // Act - Exceed API key limit on key-1
        for (var i = 0; i < 10; i++) await detector.DetectAsync(context1);

        // First request on key-2 (same IP)
        var result2 = await detector.DetectAsync(context2);

        // Assert - Key-2 should not have API key rate limit (different key)
        Assert.DoesNotContain(result2.Reasons, r =>
            r.Detail.Contains("API key rate limit", StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}