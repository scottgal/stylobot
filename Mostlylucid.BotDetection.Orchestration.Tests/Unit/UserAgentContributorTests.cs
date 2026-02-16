using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for UserAgentContributor to verify bot pattern detection.
/// </summary>
public class UserAgentContributorTests
{
    private readonly Mock<ILogger<UserAgentContributor>> _loggerMock;
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;
    private readonly IOptions<BotDetectionOptions> _options;

    public UserAgentContributorTests()
    {
        _loggerMock = new Mock<ILogger<UserAgentContributor>>();
        _configProviderMock = new Mock<IDetectorConfigProvider>();
        _options = Options.Create(new BotDetectionOptions());

        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>()))
            .Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>()))
            .Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);
    }

    private UserAgentContributor CreateContributor(ICompiledPatternCache? patternCache = null)
    {
        return new UserAgentContributor(
            _loggerMock.Object,
            _options,
            _configProviderMock.Object,
            patternCache);
    }

    private BlackboardState CreateState(string? userAgent = null)
    {
        var httpContext = new DefaultHttpContext();
        if (userAgent != null)
            httpContext.Request.Headers.UserAgent = userAgent;

        var signalDict = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    [Theory]
    [InlineData("Scrapy/2.7.0 (+https://scrapy.org)", "Scrapy")]
    [InlineData("python-requests/2.28.0", "python-requests")]
    [InlineData("curl/7.88.1", "curl")]
    public async Task ContributeAsync_KnownBotPattern_ReturnsBotContribution(string userAgent, string expectedBotName)
    {
        // No pattern cache — test the built-in IsCommonBotPattern
        var contributor = CreateContributor(patternCache: null);
        var state = CreateState(userAgent);

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 1, $"Expected at least 1 contribution for '{userAgent}', got {contributions.Count}");

        var botContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta > 0);
        Assert.NotNull(botContrib);
        Assert.True(botContrib.ConfidenceDelta > 0.5, $"Expected bot contribution delta > 0.5 for '{userAgent}', got {botContrib.ConfidenceDelta}");
        Assert.Equal("UserAgent", botContrib.Category);
    }

    [Theory]
    [InlineData("Scrapy/2.7.0 (+https://scrapy.org)")]
    [InlineData("python-requests/2.28.0")]
    [InlineData("curl/7.88.1")]
    public async Task ContributeAsync_KnownBotPatternWithPatternCache_ReturnsBotContribution(string userAgent)
    {
        // WITH pattern cache (like production)
        var patternCacheMock = new Mock<ICompiledPatternCache>();
        string? matchedPattern = null;
        patternCacheMock.Setup(c => c.MatchesAnyPattern(It.IsAny<string>(), out matchedPattern))
            .Returns(false); // Pattern cache doesn't match, fall through to IsCommonBotPattern

        var contributor = CreateContributor(patternCacheMock.Object);
        var state = CreateState(userAgent);

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 1, $"Expected at least 1 contribution for '{userAgent}', got {contributions.Count}");

        var botContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta > 0);
        Assert.NotNull(botContrib);
        Assert.True(botContrib.ConfidenceDelta > 0.5, $"Expected bot contribution delta > 0.5 for '{userAgent}', got {botContrib.ConfidenceDelta}");
    }

    [Theory]
    [InlineData("Scrapy/2.7.0 (+https://scrapy.org)")]
    [InlineData("python-requests/2.28.0")]
    [InlineData("curl/7.88.1")]
    public async Task ContributeAsync_PatternCacheMatchesTrue_StillReturnsBotContribution(string userAgent)
    {
        // When pattern cache matches (returns true), the code should STILL produce a bot contribution
        var patternCacheMock = new Mock<ICompiledPatternCache>();
        string? matchedPattern = "test-pattern";
        patternCacheMock.Setup(c => c.MatchesAnyPattern(It.IsAny<string>(), out matchedPattern))
            .Returns(true);

        var contributor = CreateContributor(patternCacheMock.Object);
        var state = CreateState(userAgent);

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 1, $"Expected at least 1 contribution for '{userAgent}', got {contributions.Count}");

        var botContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta > 0);
        Assert.NotNull(botContrib);
        Assert.True(botContrib.ConfidenceDelta > 0.5, $"Expected bot contribution delta > 0.5 for '{userAgent}', got {botContrib.ConfidenceDelta}");
        Assert.Equal("UserAgent", botContrib.Category);
    }

    [Fact]
    public async Task ContributeAsync_NormalBrowserWithPatternCacheNoMatch_ReturnsHumanContribution()
    {
        // Normal browser - pattern cache doesn't match - should be human
        var patternCacheMock = new Mock<ICompiledPatternCache>();
        string? matchedPattern = null;
        patternCacheMock.Setup(c => c.MatchesAnyPattern(It.IsAny<string>(), out matchedPattern))
            .Returns(false);

        var contributor = CreateContributor(patternCacheMock.Object);
        var state = CreateState("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 1);
        var humanContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta <= 0);
        Assert.NotNull(humanContrib);
    }

    [Fact]
    public async Task ContributeAsync_NormalBrowserButPatternCacheMatchesFalsePositive_ReturnsBotContribution()
    {
        // Normal browser UA BUT pattern cache falsely matches - should still return bot contribution
        // This tests the case where broad patterns like \+http or \.org match a normal-looking UA
        var patternCacheMock = new Mock<ICompiledPatternCache>();
        string? matchedPattern = "\\.com"; // False positive from broad regex
        patternCacheMock.Setup(c => c.MatchesAnyPattern(It.IsAny<string>(), out matchedPattern))
            .Returns(true);

        var contributor = CreateContributor(patternCacheMock.Object);
        var state = CreateState("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        // Even with false positive from pattern cache, it should still be marked as bot
        Assert.True(contributions.Count >= 1);
        var botContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta > 0);
        Assert.NotNull(botContrib);
    }

    [Fact]
    public async Task ContributeAsync_NormalBrowser_ReturnsHumanContribution()
    {
        var contributor = CreateContributor(patternCache: null);
        var state = CreateState("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 1);
        var humanContrib = contributions.FirstOrDefault(c => c.ConfidenceDelta <= 0);
        Assert.NotNull(humanContrib);
    }
}
