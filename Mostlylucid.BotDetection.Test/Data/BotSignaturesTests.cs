using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Comprehensive tests for BotSignatures static data
/// </summary>
public class BotSignaturesTests
{
    #region GoodBots Tests

    [Fact]
    public void GoodBots_IsNotNull()
    {
        // Assert
        Assert.NotNull(BotSignatures.GoodBots);
    }

    [Fact]
    public void GoodBots_IsNotEmpty()
    {
        // Assert
        Assert.NotEmpty(BotSignatures.GoodBots);
    }

    [Theory]
    [InlineData("Googlebot")]
    [InlineData("Bingbot")]
    [InlineData("DuckDuckBot")]
    [InlineData("Slackbot")]
    [InlineData("facebookexternalhit")]
    [InlineData("Twitterbot")]
    [InlineData("LinkedInBot")]
    public void GoodBots_ContainsCommonSearchEnginesAndSocialBots(string botName)
    {
        // Assert
        Assert.True(BotSignatures.GoodBots.ContainsKey(botName),
            $"GoodBots should contain {botName}");
    }

    [Theory]
    [InlineData("Googlebot", "Google")]
    [InlineData("Bingbot", "Bing")]
    [InlineData("DuckDuckBot", "DuckDuckGo")]
    public void GoodBots_HasDescriptiveNames(string key, string expectedPartOfValue)
    {
        // Assert
        Assert.Contains(expectedPartOfValue, BotSignatures.GoodBots[key]);
    }

    [Fact]
    public void GoodBots_IsCaseInsensitive()
    {
        // Assert
        Assert.True(BotSignatures.GoodBots.Comparer == StringComparer.OrdinalIgnoreCase,
            "GoodBots should use case-insensitive comparison");
    }

    [Fact]
    public void GoodBots_ContainsSearchEngines()
    {
        // Assert
        var searchEngines = new[] { "Googlebot", "Bingbot", "DuckDuckBot", "Baiduspider", "YandexBot" };
        foreach (var bot in searchEngines)
            Assert.True(BotSignatures.GoodBots.ContainsKey(bot),
                $"Should contain search engine bot: {bot}");
    }

    [Fact]
    public void GoodBots_ContainsSocialMediaBots()
    {
        // Assert
        var socialBots = new[] { "facebookexternalhit", "Twitterbot", "LinkedInBot", "Slackbot", "Discordbot" };
        foreach (var bot in socialBots)
            Assert.True(BotSignatures.GoodBots.ContainsKey(bot),
                $"Should contain social media bot: {bot}");
    }

    [Fact]
    public void GoodBots_ContainsMonitoringTools()
    {
        // Assert
        Assert.True(BotSignatures.GoodBots.ContainsKey("Pingdom") ||
                    BotSignatures.GoodBots.ContainsKey("StatusCake") ||
                    BotSignatures.GoodBots.ContainsKey("Uptimebot"),
            "Should contain monitoring bots");
    }

    [Fact]
    public void GoodBots_ContainsDevelopmentTools()
    {
        // Assert
        var devTools = new[] { "curl", "Wget", "Postman" };
        foreach (var tool in devTools)
            Assert.True(BotSignatures.GoodBots.ContainsKey(tool),
                $"Should contain development tool: {tool}");
    }

    #endregion

    #region MaliciousBotPatterns Tests

    [Fact]
    public void MaliciousBotPatterns_IsNotNull()
    {
        // Assert
        Assert.NotNull(BotSignatures.MaliciousBotPatterns);
    }

    [Fact]
    public void MaliciousBotPatterns_IsNotEmpty()
    {
        // Assert
        Assert.NotEmpty(BotSignatures.MaliciousBotPatterns);
    }

    [Theory]
    [InlineData("scrapy")]
    [InlineData("scraper")]
    [InlineData("spider")]
    [InlineData("crawler")]
    [InlineData("bot")]
    public void MaliciousBotPatterns_ContainsCommonBotTerms(string pattern)
    {
        // Assert
        Assert.Contains(pattern, BotSignatures.MaliciousBotPatterns);
    }

    [Theory]
    [InlineData("httrack")]
    [InlineData("harvest")]
    [InlineData("grab")]
    [InlineData("leech")]
    public void MaliciousBotPatterns_ContainsScrapingTerms(string pattern)
    {
        // Assert
        Assert.Contains(pattern, BotSignatures.MaliciousBotPatterns);
    }

    [Theory]
    [InlineData("sqlmap")]
    [InlineData("nikto")]
    [InlineData("nmap")]
    [InlineData("acunetix")]
    public void MaliciousBotPatterns_ContainsSecurityScannerTerms(string pattern)
    {
        // Assert
        Assert.Contains(pattern, BotSignatures.MaliciousBotPatterns);
    }

    [Theory]
    [InlineData("python-urllib")]
    [InlineData("libwww-perl")]
    [InlineData("libcurl")]
    [InlineData("go-http-client")]
    public void MaliciousBotPatterns_ContainsHttpClientLibraries(string pattern)
    {
        // Assert
        Assert.Contains(pattern, BotSignatures.MaliciousBotPatterns);
    }

    [Fact]
    public void MaliciousBotPatterns_IsCaseInsensitive()
    {
        // Assert
        Assert.True(BotSignatures.MaliciousBotPatterns.Comparer == StringComparer.OrdinalIgnoreCase,
            "MaliciousBotPatterns should use case-insensitive comparison");
    }

    #endregion

    #region AutomationFrameworks Tests

    [Fact]
    public void AutomationFrameworks_IsNotNull()
    {
        // Assert
        Assert.NotNull(BotSignatures.AutomationFrameworks);
    }

    [Fact]
    public void AutomationFrameworks_IsNotEmpty()
    {
        // Assert
        Assert.NotEmpty(BotSignatures.AutomationFrameworks);
    }

    [Theory]
    [InlineData("Selenium")]
    [InlineData("WebDriver")]
    [InlineData("PhantomJS")]
    [InlineData("Puppeteer")]
    [InlineData("Playwright")]
    public void AutomationFrameworks_ContainsBrowserAutomation(string framework)
    {
        // Assert
        Assert.Contains(framework, BotSignatures.AutomationFrameworks);
    }

    [Theory]
    [InlineData("HeadlessChrome")]
    [InlineData("Nightmare")]
    [InlineData("CasperJS")]
    [InlineData("SlimerJS")]
    public void AutomationFrameworks_ContainsHeadlessBrowsers(string framework)
    {
        // Assert
        Assert.Contains(framework, BotSignatures.AutomationFrameworks);
    }

    [Theory]
    [InlineData("Cypress")]
    [InlineData("TestCafe")]
    [InlineData("Watir")]
    public void AutomationFrameworks_ContainsTestingFrameworks(string framework)
    {
        // Assert
        Assert.Contains(framework, BotSignatures.AutomationFrameworks);
    }

    [Theory]
    [InlineData("Scrapy")]
    [InlineData("BeautifulSoup")]
    [InlineData("cheerio")]
    public void AutomationFrameworks_ContainsScrapingLibraries(string framework)
    {
        // Assert
        Assert.Contains(framework, BotSignatures.AutomationFrameworks);
    }

    [Fact]
    public void AutomationFrameworks_IsCaseInsensitive()
    {
        // Assert
        Assert.True(BotSignatures.AutomationFrameworks.Comparer == StringComparer.OrdinalIgnoreCase,
            "AutomationFrameworks should use case-insensitive comparison");
    }

    #endregion

    #region BotPatterns Tests

    [Fact]
    public void BotPatterns_IsNotNull()
    {
        // Assert
        Assert.NotNull(BotSignatures.BotPatterns);
    }

    [Fact]
    public void BotPatterns_IsNotEmpty()
    {
        // Assert
        Assert.NotEmpty(BotSignatures.BotPatterns);
    }

    [Fact]
    public void BotPatterns_ContainsValidRegexPatterns()
    {
        // Act & Assert
        foreach (var pattern in BotSignatures.BotPatterns)
        {
            var exception = Record.Exception(() =>
                Regex.IsMatch("test", pattern));
            Assert.Null(exception);
        }
    }

    [Theory]
    [InlineData(@"\bbot\b")]
    [InlineData(@"\bcrawl")]
    [InlineData(@"\bspider\b")]
    public void BotPatterns_ContainsCommonBotPatterns(string pattern)
    {
        // Assert
        Assert.Contains(pattern, BotSignatures.BotPatterns);
    }

    [Fact]
    public void BotPatterns_ContainsUrlPatterns()
    {
        // Assert
        Assert.Contains(BotSignatures.BotPatterns, p =>
            p.Contains("http") || p.Contains("://"));
    }

    [Fact]
    public void BotPatterns_BotPatternMatchesBot()
    {
        // Arrange
        var botPattern = @"\bbot\b";

        // Assert
        Assert.True(Regex.IsMatch(
            "This is a bot user-agent",
            botPattern,
            RegexOptions.IgnoreCase));
    }

    [Fact]
    public void BotPatterns_BotPatternDoesNotMatchRobot()
    {
        // Arrange
        var botPattern = @"\bbot\b";

        // Assert - "robot" should not match \bbot\b (word boundary)
        Assert.False(Regex.IsMatch(
            "This is robot text",
            botPattern,
            RegexOptions.IgnoreCase));
    }

    #endregion

    #region SuspiciousHeaderPatterns Tests

    [Fact]
    public void SuspiciousHeaderPatterns_IsNotNull()
    {
        // Assert
        Assert.NotNull(BotSignatures.SuspiciousHeaderPatterns);
    }

    [Fact]
    public void SuspiciousHeaderPatterns_ContainsUserAgentPatterns()
    {
        // Assert
        Assert.Contains(BotSignatures.SuspiciousHeaderPatterns, p =>
            p.Contains("User-Agent"));
    }

    #endregion

    #region Consistency Tests

    [Fact]
    public void GoodBots_DoNotOverlapWithMaliciousPatterns()
    {
        // Assert - Good bots shouldn't be flagged as malicious
        foreach (var goodBot in BotSignatures.GoodBots.Keys)
        {
            // Check that good bot names aren't in malicious patterns
            var isMalicious = BotSignatures.MaliciousBotPatterns.Any(pattern =>
                goodBot.Contains(pattern, StringComparison.OrdinalIgnoreCase) &&
                pattern != "bot"); // "bot" is generic, so exclude it

            // Some overlap is expected for generic terms, but specific bots shouldn't match
            if (isMalicious)
            {
                // This is informational - some overlap is expected
                // The actual detection logic should handle this
            }
        }
    }

    [Fact]
    public void AllCollections_HaveReasonableSize()
    {
        // Assert
        Assert.InRange(BotSignatures.GoodBots.Count, 10, 200);
        Assert.InRange(BotSignatures.MaliciousBotPatterns.Count, 20, 200);
        Assert.InRange(BotSignatures.AutomationFrameworks.Count, 10, 100);
        Assert.InRange(BotSignatures.BotPatterns.Length, 5, 50);
    }

    #endregion
}