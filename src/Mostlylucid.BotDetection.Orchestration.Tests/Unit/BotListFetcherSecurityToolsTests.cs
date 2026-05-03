using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for BotListFetcher.GetSecurityToolPatternsAsync.
///     Tests fetching security tool patterns from external sources.
/// </summary>
public class BotListFetcherSecurityToolsTests : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly Mock<ILogger<BotListFetcher>> _loggerMock;
    private readonly BotDetectionOptions _options;

    public BotListFetcherSecurityToolsTests()
    {
        _loggerMock = new Mock<ILogger<BotListFetcher>>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _options = new BotDetectionOptions
        {
            DataSources = new DataSourcesOptions
            {
                ScannerUserAgents = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://example.com/scanners.json"
                },
                CoreRuleSetScanners = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://example.com/crs-scanners.txt"
                }
            }
        };
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private (BotListFetcher Fetcher, Mock<HttpMessageHandler> HandlerMock) CreateFetcher()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(handlerMock.Object);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        var fetcher = new BotListFetcher(
            httpClientFactory.Object,
            _cache,
            _loggerMock.Object,
            Options.Create(_options));

        return (fetcher, handlerMock);
    }

    // ==========================================
    // digininja/scanner_user_agents JSON Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_ParsesDigininjaJson_Correctly()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        // Actual digininja format: {ua: "full UA string", scanner: "ToolName", version: "...", "last seen": "..."}
        // The fetcher extracts unique scanner names and uses them as patterns
        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new { ua = "Mozilla/5.0 (some UA)", scanner = "CustomTool1" },
            new { ua = "Another UA string", scanner = "CustomTool2" },
            new { ua = "Yet another", scanner = "CustomTool3" }
        });

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should contain extracted scanner names (lowercased) + fallback patterns
        Assert.True(patterns.Count >= 15); // At least fallback patterns

        // Check digininja patterns were extracted
        Assert.Contains(patterns, p => p.Pattern == "customtool1" && p.Source == "digininja/scanner_user_agents");
        Assert.Contains(patterns, p => p.Pattern == "customtool2" && p.Source == "digininja/scanner_user_agents");
        Assert.Contains(patterns, p => p.Pattern == "customtool3" && p.Source == "digininja/scanner_user_agents");

        // Fallback patterns should always be present
        Assert.Contains(patterns, p => p.Pattern == "burp");
        Assert.Contains(patterns, p => p.Pattern == "acunetix");
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_CoreRuleSetPatternsAreRegexDetected()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        // digininja extracts scanner names (not patterns)
        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new { ua = "some ua", scanner = "SimpleScanner" }
        });

        // CoreRuleSet can have regex patterns
        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, @"sqlmap[\s/]?\d
simple-pattern");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert
        var regexPattern = patterns.First(p => p.Pattern.Contains(@"sqlmap[\s/]?\d"));
        Assert.True(regexPattern.IsRegex);

        var simplePattern = patterns.First(p => p.Pattern == "simple-pattern");
        Assert.False(simplePattern.IsRegex);
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_ExtractsUniqueScannersFromDigininja()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        // Digininja format uses "scanner" field for tool name
        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new { ua = "UA1", scanner = "Nikto" },
            new { ua = "UA2", scanner = "Nikto" }, // Duplicate scanner name
            new { ua = "UA3", scanner = "Nessus" }
        });

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Nikto appears only once (deduplicated by scanner name)
        var niktoPatterns = patterns.Where(p => p.Pattern == "nikto").ToList();
        Assert.Single(niktoPatterns);

        // Nessus should be present
        Assert.Contains(patterns, p => p.Pattern == "nessus");
    }

    // ==========================================
    // OWASP CoreRuleSet Text Format Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_ParsesCoreRuleSetText_Correctly()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, "[]");
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, @"
# This is a comment
uniquetool1
uniquetool2
uniquetool3

# Another comment
uniquetool4
");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Contains CoreRuleSet patterns + fallback patterns
        Assert.True(patterns.Count >= 15); // At least fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "uniquetool1" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "uniquetool2" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "uniquetool3" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "uniquetool4" && p.Source == "OWASP/CoreRuleSet");
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_SkipsCommentsAndEmptyLines()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, "[]");
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, @"
# Comment line
   # Indented comment
uniquepattern1


uniquepattern2
");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Contains unique patterns + fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "uniquepattern1");
        Assert.Contains(patterns, p => p.Pattern == "uniquepattern2");
        // Comments and empty lines should NOT be in patterns
        Assert.DoesNotContain(patterns, p => p.Pattern.StartsWith("#"));
        Assert.DoesNotContain(patterns, p => string.IsNullOrWhiteSpace(p.Pattern));
    }

    // ==========================================
    // Deduplication Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_DeduplicatesPatterns()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        // digininja format: scanner name becomes pattern (lowercased)
        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new { ua = "UA1", scanner = "UniqueScanner" },
            new { ua = "UA2", scanner = "UNIQUESCANNER" } // Duplicate (case-insensitive)
        });

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!,
            "uniquescanner\nUNIQUESCANNER"); // More duplicates

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have only one uniquescanner pattern (deduplicated)
        var uniqueScannerPatterns =
            patterns.Where(p => p.Pattern.Equals("uniquescanner", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(uniqueScannerPatterns);
    }

    // ==========================================
    // Caching Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_UsesCachedResults()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new { ua = "UA1", scanner = "CustomScanner" }
        });

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "");

        // Act - Call twice
        var result1 = await fetcher.GetSecurityToolPatternsAsync();
        var result2 = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should return same instance (cached)
        Assert.Same(result1, result2);

        // HTTP should only be called once for each URL
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(2), // Once for each URL
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    // ==========================================
    // Error Handling Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_ReturnsFromOtherSource_WhenOneSourceFails()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();

        // Scanner user agents fails
        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, HttpStatusCode.InternalServerError);
        // CoreRuleSet succeeds with unique patterns
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "uniquepattern1\nuniquepattern2");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have patterns from CRS + fallback patterns
        Assert.True(patterns.Count >= 15); // At least fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "uniquepattern1" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "uniquepattern2" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "burp" && p.Source == "fallback");
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_ReturnsFallback_WhenAllSourcesFail()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, HttpStatusCode.InternalServerError);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!,
            HttpStatusCode.InternalServerError);

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have fallback patterns
        Assert.NotEmpty(patterns);
        Assert.All(patterns, p => Assert.Equal("fallback", p.Source));
        Assert.Contains(patterns, p => p.Pattern == "sqlmap");
        Assert.Contains(patterns, p => p.Pattern == "nikto");
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_HandlesInvalidJson()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, "not valid json{{{");
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "uniquecrstool");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have patterns from CRS + fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "uniquecrstool" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "burp" && p.Source == "fallback");
    }

    // ==========================================
    // Configuration Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_SkipsDisabledSources()
    {
        // Arrange
        _options.DataSources.ScannerUserAgents.Enabled = false;
        var (fetcher, handlerMock) = CreateFetcher();

        // Only CRS should be called
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "uniquecrsonlytool");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have CRS + fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "uniquecrsonlytool" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "burp" && p.Source == "fallback");

        // Scanner user agents should not be called
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == _options.DataSources.ScannerUserAgents.Url),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_SkipsSourcesWithEmptyUrl()
    {
        // Arrange
        _options.DataSources.ScannerUserAgents.Url = "";
        var (fetcher, handlerMock) = CreateFetcher();

        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "uniqueemptyurltool");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have CRS + fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "uniqueemptyurltool" && p.Source == "OWASP/CoreRuleSet");
        Assert.Contains(patterns, p => p.Pattern == "burp" && p.Source == "fallback");
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_ReturnsFallback_WhenBothSourcesDisabled()
    {
        // Arrange
        _options.DataSources.ScannerUserAgents.Enabled = false;
        _options.DataSources.CoreRuleSetScanners.Enabled = false;
        var (fetcher, handlerMock) = CreateFetcher();

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should return fallback patterns
        Assert.NotEmpty(patterns);
        Assert.All(patterns, p => Assert.Equal("fallback", p.Source));
    }

    // ==========================================
    // Pattern Content Tests
    // ==========================================

    [Fact]
    public async Task GetSecurityToolPatternsAsync_TrimsWhitespace()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        // digininja format: scanner name becomes pattern (lowercased, trimmed)
        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new { ua = "UA1", scanner = "  CustomTrimmedTool  " }
        });

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "  uniquecrspattern  ");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Patterns should be trimmed
        Assert.Contains(patterns, p => p.Pattern == "customtrimmedtool");
        Assert.Contains(patterns, p => p.Pattern == "uniquecrspattern");
    }

    [Fact]
    public async Task GetSecurityToolPatternsAsync_SkipsEmptyScannerNames()
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();
        // digininja format: entries with empty/null scanner are skipped
        var jsonContent = JsonSerializer.Serialize(new object[]
        {
            new { ua = "UA1", scanner = "" },
            new { ua = "UA2", scanner = "ValidScanner" },
            new { ua = "UA3", scanner = (string?)null }
        });

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, jsonContent);
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, "");

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Should have validscanner from digininja + fallback patterns
        Assert.Contains(patterns, p => p.Pattern == "validscanner" && p.Source == "digininja/scanner_user_agents");
        // Should not have empty patterns
        Assert.DoesNotContain(patterns, p => string.IsNullOrWhiteSpace(p.Pattern));
    }

    // ==========================================
    // Name Inference Tests (for CoreRuleSet patterns without explicit names)
    // ==========================================

    [Theory]
    [InlineData("uniquenikto/2.1.6", "Nikto")]
    [InlineData("uniquenmap scripting engine", "Nmap")]
    [InlineData("uniquegobuster/3.1", "Gobuster")]
    [InlineData("uniquehydra-http", "Hydra")]
    [InlineData("uniquemetasploit framework", "Metasploit")]
    public async Task GetSecurityToolPatternsAsync_InfersToolNameForCoreRuleSetPatterns(string pattern,
        string expectedName)
    {
        // Arrange
        var (fetcher, handlerMock) = CreateFetcher();

        SetupHttpResponse(handlerMock, _options.DataSources.ScannerUserAgents.Url!, "[]");
        SetupHttpResponse(handlerMock, _options.DataSources.CoreRuleSetScanners.Url!, pattern);

        // Act
        var patterns = await fetcher.GetSecurityToolPatternsAsync();

        // Assert - Find the pattern from CoreRuleSet
        var crsPattern = patterns.FirstOrDefault(p => p.Pattern == pattern && p.Source == "OWASP/CoreRuleSet");
        Assert.NotNull(crsPattern);
        Assert.Equal(expectedName, crsPattern.Name);
    }

    // ==========================================
    // Helper Methods
    // ==========================================

    private static void SetupHttpResponse(Mock<HttpMessageHandler> handlerMock, string url, string content)
    {
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(content)
            });
    }

    private static void SetupHttpResponse(Mock<HttpMessageHandler> handlerMock, string url, HttpStatusCode statusCode)
    {
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode
            });
    }
}