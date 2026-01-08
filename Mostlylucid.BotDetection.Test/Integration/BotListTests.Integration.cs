using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Integration;

/// <summary>
///     Integration tests that download actual bot lists from external sources
///     and verify the detection system works correctly against them.
///     These tests require network access and may be slow.
/// </summary>
[Trait("Category", "Integration")]
public class BotListIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _httpClient = new();
    private List<string> _awsIpRanges = new();
    private List<string> _cloudflareIpRanges = new();
    private List<CrawlerUserAgent> _crawlerUserAgents = new();
    private List<string> _gcpIpRanges = new();
    private List<string> _isBotPatterns = new();

    public async Task InitializeAsync()
    {
        // Download all lists in parallel
        var tasks = new[]
        {
            DownloadIsBotPatternsAsync(),
            DownloadCrawlerUserAgentsAsync(),
            DownloadAwsIpRangesAsync(),
            DownloadGcpIpRangesAsync(),
            DownloadCloudflareIpRangesAsync()
        };

        await Task.WhenAll(tasks);
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    #region End-to-End Detection Tests

    [Theory]
    [InlineData("Googlebot/2.1", true)]
    [InlineData("bingbot/2.0", true)]
    [InlineData("curl/7.68.0", true)]
    [InlineData("python-requests/2.25.1", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0", false)]
    public async Task EndToEnd_DetectsBotsCorrectly(string userAgent, bool expectBot)
    {
        // Arrange
        var fetcher = CreateBotListFetcher();
        var patterns = await fetcher.GetBotPatternsAsync();

        // Act
        var isBot = patterns.Any(pattern =>
        {
            try
            {
                return Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        // Assert
        Assert.Equal(expectBot, isBot);
    }

    #endregion

    #region Test Helpers

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }

    #endregion

    #region Download Methods

    private async Task DownloadIsBotPatternsAsync()
    {
        try
        {
            var url = "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json";
            var json = await _httpClient.GetStringAsync(url);
            _isBotPatterns = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            // Log but don't fail - some tests can still run
            Console.WriteLine($"Failed to download IsBot patterns: {ex.Message}");
        }
    }

    private async Task DownloadCrawlerUserAgentsAsync()
    {
        try
        {
            var url = "https://raw.githubusercontent.com/monperrus/crawler-user-agents/master/crawler-user-agents.json";
            var json = await _httpClient.GetStringAsync(url);
            _crawlerUserAgents = JsonSerializer.Deserialize<List<CrawlerUserAgent>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CrawlerUserAgent>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download crawler-user-agents: {ex.Message}");
        }
    }

    private async Task DownloadAwsIpRangesAsync()
    {
        try
        {
            var url = "https://ip-ranges.amazonaws.com/ip-ranges.json";
            var json = await _httpClient.GetStringAsync(url);
            var data = JsonSerializer.Deserialize<AwsIpRangesResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _awsIpRanges = data?.Prefixes?
                .Where(p => !string.IsNullOrEmpty(p.IpPrefix))
                .Select(p => p.IpPrefix!)
                .Take(100) // Limit for test performance
                .ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download AWS IP ranges: {ex.Message}");
        }
    }

    private async Task DownloadGcpIpRangesAsync()
    {
        try
        {
            var url = "https://www.gstatic.com/ipranges/cloud.json";
            var json = await _httpClient.GetStringAsync(url);
            var data = JsonSerializer.Deserialize<GcpIpRangesResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _gcpIpRanges = data?.Prefixes?
                .Where(p => !string.IsNullOrEmpty(p.Ipv4Prefix))
                .Select(p => p.Ipv4Prefix!)
                .Take(100)
                .ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download GCP IP ranges: {ex.Message}");
        }
    }

    private async Task DownloadCloudflareIpRangesAsync()
    {
        try
        {
            var url = "https://www.cloudflare.com/ips-v4";
            var text = await _httpClient.GetStringAsync(url);
            _cloudflareIpRanges = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download Cloudflare IP ranges: {ex.Message}");
        }
    }

    #endregion

    #region IsBot Pattern Tests

    [Fact]
    public void IsBotPatterns_Downloaded_HasPatterns()
    {
        Assert.NotEmpty(_isBotPatterns);
        Assert.True(_isBotPatterns.Count > 100, $"Expected >100 patterns, got {_isBotPatterns.Count}");
    }

    [Fact]
    public void IsBotPatterns_AllAreValidRegex()
    {
        var invalidPatterns = new List<string>();

        foreach (var pattern in _isBotPatterns)
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (RegexParseException)
            {
                invalidPatterns.Add(pattern);
            }

        Assert.Empty(invalidPatterns);
    }

    [Theory]
    [InlineData("Googlebot/2.1 (+http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("curl/7.68.0")]
    [InlineData("python-requests/2.25.1")]
    [InlineData("Wget/1.21")]
    public void IsBotPatterns_MatchKnownBots(string userAgent)
    {
        var matched = _isBotPatterns.Any(pattern =>
        {
            try
            {
                return Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        Assert.True(matched, $"User-Agent '{userAgent}' should match at least one IsBot pattern");
    }

    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1")]
    public void IsBotPatterns_DoNotMatchRealBrowsers(string userAgent)
    {
        var matchedPatterns = _isBotPatterns
            .Where(pattern =>
            {
                try
                {
                    return Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .ToList();

        Assert.Empty(matchedPatterns);
    }

    #endregion

    #region Crawler User Agents Tests

    [Fact]
    public void CrawlerUserAgents_Downloaded_HasEntries()
    {
        Assert.NotEmpty(_crawlerUserAgents);
        Assert.True(_crawlerUserAgents.Count > 50, $"Expected >50 crawlers, got {_crawlerUserAgents.Count}");
    }

    [Fact]
    public void CrawlerUserAgents_AllHavePatterns()
    {
        var missingPatterns = _crawlerUserAgents
            .Where(c => string.IsNullOrEmpty(c.Pattern))
            .ToList();

        Assert.Empty(missingPatterns);
    }

    [Fact]
    public void CrawlerUserAgents_AllPatternsAreValidRegex()
    {
        var invalidPatterns = new List<string>();

        foreach (var crawler in _crawlerUserAgents)
        {
            if (string.IsNullOrEmpty(crawler.Pattern)) continue;

            try
            {
                _ = new Regex(crawler.Pattern, RegexOptions.IgnoreCase);
            }
            catch (RegexParseException)
            {
                invalidPatterns.Add(crawler.Pattern);
            }
        }

        Assert.Empty(invalidPatterns);
    }

    [Fact]
    public void CrawlerUserAgents_ContainsWellKnownCrawlers()
    {
        var patterns = _crawlerUserAgents.Select(c => c.Pattern ?? "").ToList();

        // Check for well-known crawlers
        Assert.Contains(patterns, p => p.Contains("Googlebot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(patterns, p => p.Contains("bingbot", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region IP Range Tests

    [Fact]
    public void AwsIpRanges_Downloaded_HasRanges()
    {
        Assert.NotEmpty(_awsIpRanges);
    }

    [Fact]
    public void GcpIpRanges_Downloaded_HasRanges()
    {
        Assert.NotEmpty(_gcpIpRanges);
    }

    [Fact]
    public void CloudflareIpRanges_Downloaded_HasRanges()
    {
        Assert.NotEmpty(_cloudflareIpRanges);
    }

    [Fact]
    public void AllIpRanges_AreValidCidr()
    {
        var allRanges = _awsIpRanges
            .Concat(_gcpIpRanges)
            .Concat(_cloudflareIpRanges)
            .ToList();

        var invalidRanges = new List<string>();

        foreach (var range in allRanges)
            if (!IsValidCidr(range))
                invalidRanges.Add(range);

        Assert.Empty(invalidRanges);
    }

    private static bool IsValidCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2) return false;

        if (!IPAddress.TryParse(parts[0], out _)) return false;

        if (!int.TryParse(parts[1], out var prefix)) return false;

        // IPv4 prefix: 0-32, IPv6 prefix: 0-128
        return prefix >= 0 && prefix <= 128;
    }

    #endregion

    #region UserAgentDetector Integration Tests

    [Fact]
    public async Task UserAgentDetector_WithDownloadedPatterns_RecognizesGooglebot()
    {
        // Arrange - Googlebot is a whitelisted good bot, so UserAgentDetector
        // returns 0 confidence (it's verified/whitelisted, not flagged as suspicious)
        var detector = CreateUserAgentDetector();
        var context = CreateHttpContext("Googlebot/2.1 (+http://www.google.com/bot.html)");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - Googlebot is whitelisted, so confidence is 0 (known good bot)
        Assert.Equal(0, result.Confidence);
        Assert.Equal(BotType.VerifiedBot, result.BotType);
        Assert.Equal("Google Search", result.BotName);
    }

    [Fact]
    public async Task UserAgentDetector_WithDownloadedPatterns_DetectsCurl()
    {
        // Arrange
        var detector = CreateUserAgentDetector();
        var context = CreateHttpContext("curl/7.68.0");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence > 0, "curl should be detected as a bot");
    }

    [Fact]
    public async Task UserAgentDetector_WithDownloadedPatterns_DoesNotDetectChrome()
    {
        // Arrange
        var detector = CreateUserAgentDetector();
        var context = CreateHttpContext(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(0, result.Confidence);
    }

    [Theory]
    [InlineData("Scrapy/2.5.0 (+https://scrapy.org)")] // Contains "scrapy" in MaliciousBotPatterns
    [InlineData(
        "Apache-HttpClient/4.5.13 (Java/11.0.11)")] // Contains "HttpClient" in AutomationFrameworks + "Java/" in MaliciousBotPatterns
    [InlineData("Go-http-client/1.1")] // Contains "go-http-client" in MaliciousBotPatterns
    public async Task UserAgentDetector_DetectsSuspiciousHttpLibraries(string userAgent)
    {
        // Arrange
        var detector = CreateUserAgentDetector();
        var context = CreateHttpContext(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence > 0, $"'{userAgent}' should be detected as suspicious");
    }

    [Theory]
    [InlineData("curl/7.68.0")] // Short UA (11 chars) + version pattern = detected as bot
    [InlineData("Wget/1.21")] // Short UA (9 chars) + version pattern = detected as bot
    public async Task UserAgentDetector_DetectsShortDevToolsAsBotsNotWhitelisted(string userAgent)
    {
        // Arrange - Short dev tool UAs are detected because:
        // - Short UA length (< 20 chars) increases confidence
        // - Simple version pattern (^\w+\/[\d\.]+$) also matches
        var detector = CreateUserAgentDetector();
        var context = CreateHttpContext(userAgent);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - These match bot patterns, so confidence > 0
        Assert.True(result.Confidence > 0, $"'{userAgent}' should be detected as a bot");
    }

    [Fact]
    public async Task UserAgentDetector_DoesNotFlagLongerDevTools()
    {
        // Arrange - python-requests/2.28.0 is 22 chars (> 20), so it doesn't trigger
        // the short UA check. It's in GoodBots but NOT in WhitelistedBotPatterns.
        // Since it doesn't match malicious patterns or automation frameworks,
        // it returns confidence 0.
        var detector = CreateUserAgentDetector();
        var context = CreateHttpContext("python-requests/2.28.0");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - Doesn't match any detection patterns, so confidence is 0
        // This might be a gap in detection - consider adding python-requests to patterns
        Assert.Equal(0, result.Confidence);
    }

    #endregion

    #region BotListFetcher Integration Tests

    [Fact]
    public async Task BotListFetcher_GetBotPatterns_ReturnsPatterns()
    {
        // Arrange
        var fetcher = CreateBotListFetcher();

        // Act
        var patterns = await fetcher.GetBotPatternsAsync();

        // Assert
        Assert.NotEmpty(patterns);
        Assert.True(patterns.Count > 100, $"Expected >100 patterns, got {patterns.Count}");
    }

    [Fact]
    public async Task BotListFetcher_GetDatacenterIpRanges_ReturnsRanges()
    {
        // Arrange
        var fetcher = CreateBotListFetcher();

        // Act
        var ranges = await fetcher.GetDatacenterIpRangesAsync();

        // Assert
        Assert.NotEmpty(ranges);
        Assert.True(ranges.Count > 50, $"Expected >50 IP ranges, got {ranges.Count}");
    }

    [Fact]
    public async Task BotListFetcher_CachesResults()
    {
        // Arrange
        var fetcher = CreateBotListFetcher();

        // Act - Call twice
        var patterns1 = await fetcher.GetBotPatternsAsync();
        var patterns2 = await fetcher.GetBotPatternsAsync();

        // Assert - Should be same reference (cached)
        Assert.Same(patterns1, patterns2);
    }

    #endregion

    #region Helper Methods

    private UserAgentDetector CreateUserAgentDetector()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            EnableUserAgentDetection = true,
            BotThreshold = 0.7
        });

        return new UserAgentDetector(
            NullLogger<UserAgentDetector>.Instance,
            options);
    }

    private BotListFetcher CreateBotListFetcher()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DataSources = new DataSourcesOptions
            {
                IsBot = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json"
                },
                AwsIpRanges = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://ip-ranges.amazonaws.com/ip-ranges.json"
                },
                GcpIpRanges = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://www.gstatic.com/ipranges/cloud.json"
                },
                CloudflareIpv4 = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://www.cloudflare.com/ips-v4"
                },
                CloudflareIpv6 = new DataSourceConfig
                {
                    Enabled = true,
                    Url = "https://www.cloudflare.com/ips-v6"
                }
            },
            ListDownloadTimeoutSeconds = 30
        });

        var httpClientFactory = new TestHttpClientFactory();
        var cache = new MemoryCache(new MemoryCacheOptions());

        return new BotListFetcher(
            httpClientFactory,
            cache,
            NullLogger<BotListFetcher>.Instance,
            options);
    }

    private static HttpContext CreateHttpContext(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Path = "/test";
        context.Request.Method = "GET";
        return context;
    }

    #endregion

    #region JSON Models

    private class CrawlerUserAgent
    {
        public string? Pattern { get; set; }
        public string? Url { get; set; }
        public string[]? Instances { get; set; }
    }

    private class AwsIpRangesResponse
    {
        public List<AwsPrefix>? Prefixes { get; set; }
    }

    private class AwsPrefix
    {
        [JsonPropertyName("ip_prefix")] public string? IpPrefix { get; set; }

        [JsonPropertyName("region")] public string? Region { get; set; }

        [JsonPropertyName("service")] public string? Service { get; set; }
    }

    private class GcpIpRangesResponse
    {
        public List<GcpPrefix>? Prefixes { get; set; }
    }

    private class GcpPrefix
    {
        [JsonPropertyName("ipv4Prefix")] public string? Ipv4Prefix { get; set; }

        [JsonPropertyName("ipv6Prefix")] public string? Ipv6Prefix { get; set; }
    }

    #endregion
}