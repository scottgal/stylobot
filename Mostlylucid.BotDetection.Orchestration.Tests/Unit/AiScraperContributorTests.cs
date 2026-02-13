using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for AiScraperContributor.
///     Tests AI bot User-Agent detection, Cloudflare signals, Web Bot Auth, and content negotiation.
/// </summary>
public class AiScraperContributorTests
{
    private readonly Mock<ILogger<AiScraperContributor>> _loggerMock;
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;

    public AiScraperContributorTests()
    {
        _loggerMock = new Mock<ILogger<AiScraperContributor>>();
        _configProviderMock = new Mock<IDetectorConfigProvider>();

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

    private AiScraperContributor CreateContributor()
    {
        return new AiScraperContributor(_loggerMock.Object, _configProviderMock.Object);
    }

    private BlackboardState CreateState(string? userAgent = null, Dictionary<string, string>? headers = null,
        string path = "/")
    {
        var httpContext = new DefaultHttpContext();

        if (userAgent != null)
            httpContext.Request.Headers.UserAgent = userAgent;

        httpContext.Request.Path = path;

        if (headers != null)
        {
            foreach (var (key, value) in headers)
                httpContext.Request.Headers[key] = value;
        }

        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = new Dictionary<string, object>(),
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    // ==========================================
    // Properties Tests
    // ==========================================

    [Fact]
    public void Name_ReturnsAiScraper()
    {
        var contributor = CreateContributor();
        Assert.Equal("AiScraper", contributor.Name);
    }

    [Fact]
    public void Priority_Is9()
    {
        var contributor = CreateContributor();
        Assert.Equal(9, contributor.Priority);
    }

    [Fact]
    public void TriggerConditions_IsEmpty_RunsInFirstWave()
    {
        var contributor = CreateContributor();
        Assert.Empty(contributor.TriggerConditions);
    }

    // ==========================================
    // Known AI Bot User-Agent Tests
    // ==========================================

    [Theory]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)", "GPTBot", "OpenAI")]
    [InlineData("ClaudeBot/1.0; +https://www.anthropic.com", "ClaudeBot", "Anthropic")]
    [InlineData("Mozilla/5.0 (compatible; PerplexityBot/1.0; +https://perplexity.ai/bot)", "PerplexityBot", "Perplexity")]
    [InlineData("Mozilla/5.0 (compatible; Bytespider; spider-feedback@bytedance.com)", "Bytespider", "ByteDance")]
    [InlineData("CCBot/2.0 (https://commoncrawl.org/faq/)", "CCBot", "Common Crawl")]
    [InlineData("Meta-ExternalAgent/1.0 (+https://developers.facebook.com/docs/sharing/bot)", "Meta-ExternalAgent", "Meta")]
    [InlineData("Mozilla/5.0 (compatible; Google-Extended/1.0)", "Google-Extended", "Google")]
    [InlineData("ChatGPT-User/1.0", "ChatGPT-User", "OpenAI")]
    [InlineData("Mozilla/5.0 (compatible; DuckAssistBot/1.0)", "DuckAssistBot", "DuckDuckGo")]
    [InlineData("Mozilla/5.0 (compatible; DeepseekBot/1.0)", "DeepseekBot", "DeepSeek")]
    [InlineData("Amazonbot/0.1 (+https://developer.amazon.com/support/amazonbot)", "Amazonbot", "Amazon")]
    public async Task ContributeAsync_KnownAiBot_DetectsAndNames(string userAgent, string expectedName,
        string expectedOperator)
    {
        var contributor = CreateContributor();
        var state = CreateState(userAgent);

        var contributions = await contributor.ContributeAsync(state);

        Assert.True(contributions.Count >= 1);
        var botContrib = contributions.First(c => c.ConfidenceDelta > 0);
        Assert.Contains(expectedName, botContrib.Reason);
        Assert.Equal(expectedName, botContrib.BotName);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey(SignalKeys.AiScraperDetected));
        Assert.True((bool)signals[SignalKeys.AiScraperDetected]);
        Assert.Equal(expectedName, signals[SignalKeys.AiScraperName]);
        Assert.Equal(expectedOperator, signals[SignalKeys.AiScraperOperator]);
    }

    [Fact]
    public async Task ContributeAsync_NormalBrowser_ReturnsNeutral()
    {
        var contributor = CreateContributor();
        var state = CreateState("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("No AI scraper signals", contributions[0].Reason);
    }

    [Fact]
    public async Task ContributeAsync_EmptyUserAgent_ReturnsNeutral()
    {
        var contributor = CreateContributor();
        var state = CreateState("");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
    }

    // ==========================================
    // Accept: text/markdown Tests (Cloudflare Markdown for Agents)
    // ==========================================

    [Fact]
    public async Task ContributeAsync_AcceptMarkdown_DetectsAiAgent()
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeBot/1.0", new Dictionary<string, string>
        {
            ["Accept"] = "text/markdown, text/html;q=0.9"
        });

        var contributions = await contributor.ContributeAsync(state);

        var markdownContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(markdownContrib);
        Assert.True(markdownContrib!.ConfidenceDelta >= 0.8);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey("aiscraper.accept_markdown"));
    }

    [Fact]
    public async Task ContributeAsync_AcceptMarkdown_WithKnownBot_DoesNotDuplicate()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0)",
            new Dictionary<string, string>
            {
                ["Accept"] = "text/markdown"
            });

        var contributions = await contributor.ContributeAsync(state);

        // Should have the known bot contribution + signal, but NOT a second Accept markdown contrib
        var markdownContribs = contributions.Count(c =>
            c.Reason?.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(0, markdownContribs); // Already detected by UA, skip markdown contribution
    }

    // ==========================================
    // Cloudflare AI Gateway Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_CfAigHeaders_DetectsAiGateway()
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeClient/1.0", new Dictionary<string, string>
        {
            ["cf-aig-cache-status"] = "MISS"
        });

        var contributions = await contributor.ContributeAsync(state);

        var aigContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("AI Gateway", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(aigContrib);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey("aiscraper.cloudflare_ai_gateway"));
    }

    // ==========================================
    // Web Bot Auth Tests (RFC 9421)
    // ==========================================

    [Fact]
    public async Task ContributeAsync_WebBotAuth_DetectsSignedBot()
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeBot/1.0", new Dictionary<string, string>
        {
            ["Signature"] = "sig1=:base64signature:",
            ["Signature-Input"] = "sig1=(\"@method\" \"@target-uri\"); tag=\"web-bot-auth\"; keyid=\"abc123\"; alg=\"ed25519\"",
            ["Signature-Agent"] = "\"https://chatgpt.com\""
        });

        var contributions = await contributor.ContributeAsync(state);

        var webBotContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("Web Bot Auth", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(webBotContrib);
        Assert.True(webBotContrib!.ConfidenceDelta >= 0.9);
        Assert.Equal("ChatGPT", webBotContrib.BotName);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey("aiscraper.web_bot_auth"));
        Assert.True(signals.ContainsKey("aiscraper.web_bot_auth_verified"));
        Assert.True((bool)signals["aiscraper.web_bot_auth_verified"]);
    }

    [Fact]
    public async Task ContributeAsync_WebBotAuth_AnthropicAgent_DetectsClaude()
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeBot/1.0", new Dictionary<string, string>
        {
            ["Signature"] = "sig1=:base64:",
            ["Signature-Input"] = "sig1=(); tag=\"web-bot-auth\"; alg=\"ed25519\"",
            ["Signature-Agent"] = "\"https://anthropic.com\""
        });

        var contributions = await contributor.ContributeAsync(state);

        var webBotContrib = contributions.FirstOrDefault(c => c.BotName == "Claude");
        Assert.NotNull(webBotContrib);
    }

    // ==========================================
    // Cloudflare Browser Rendering Tests
    // ==========================================

    [Theory]
    [InlineData("cf-brapi-request-id")]
    [InlineData("cf-biso-devtools")]
    [InlineData("cf-brapi-devtools")]
    public async Task ContributeAsync_CloudflareBrowserRendering_Detects(string headerName)
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeBot/1.0", new Dictionary<string, string>
        {
            [headerName] = "some-value"
        });

        var contributions = await contributor.ContributeAsync(state);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey("aiscraper.cloudflare_browser_rendering"));
    }

    // ==========================================
    // AI Discovery Path Tests
    // ==========================================

    [Theory]
    [InlineData("/llms.txt")]
    [InlineData("/llms-full.txt")]
    [InlineData("/.well-known/http-message-signatures-directory")]
    public async Task ContributeAsync_AiDiscoveryPath_Detects(string path)
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeBot/1.0", path: path);

        var contributions = await contributor.ContributeAsync(state);

        var pathContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains(path, StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(pathContrib);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey("aiscraper.ai_discovery_path"));
    }

    // ==========================================
    // Jina Reader API Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_JinaReaderHeaders_Detects()
    {
        var contributor = CreateContributor();
        var state = CreateState("SomeBot/1.0", new Dictionary<string, string>
        {
            ["x-respond-with"] = "markdown"
        });

        var contributions = await contributor.ContributeAsync(state);

        var jinaContrib = contributions.FirstOrDefault(c =>
            c.Reason?.Contains("Jina", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(jinaContrib);
        Assert.Equal("Jina Reader", jinaContrib!.BotName);
    }

    // ==========================================
    // Signal Emission Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_LastContributionHasAllSignals()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            "GPTBot/1.0",
            new Dictionary<string, string>
            {
                ["Accept"] = "text/markdown"
            });

        var contributions = await contributor.ContributeAsync(state);

        var lastSignals = contributions[^1].Signals;
        Assert.True(lastSignals.ContainsKey(SignalKeys.AiScraperDetected));
        Assert.True(lastSignals.ContainsKey(SignalKeys.AiScraperName));
        Assert.True(lastSignals.ContainsKey("aiscraper.accept_markdown"));
    }

    // ==========================================
    // Bot Category Tests
    // ==========================================

    [Theory]
    [InlineData("GPTBot/1.0", "Training")]
    [InlineData("OAI-SearchBot/1.0", "Search")]
    [InlineData("ChatGPT-User/1.0", "Assistant")]
    [InlineData("FirecrawlAgent/1.0", "ScrapingService")]
    public async Task ContributeAsync_CorrectCategory(string userAgent, string expectedCategory)
    {
        var contributor = CreateContributor();
        var state = CreateState(userAgent);

        var contributions = await contributor.ContributeAsync(state);

        var signals = contributions[^1].Signals;
        Assert.True(signals.ContainsKey(SignalKeys.AiScraperCategory));
        Assert.Equal(expectedCategory, signals[SignalKeys.AiScraperCategory]);
    }

    // ==========================================
    // Training Bot Type Assignment
    // ==========================================

    [Fact]
    public async Task ContributeAsync_TrainingBot_GetsBotTypeAiBot()
    {
        var contributor = CreateContributor();
        var state = CreateState("GPTBot/1.0");

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.First(c => c.ConfidenceDelta > 0);
        Assert.Equal(BotType.AiBot.ToString(), botContrib.BotType);
    }

    [Fact]
    public async Task ContributeAsync_AssistantBot_GetsBotTypeGoodBot()
    {
        var contributor = CreateContributor();
        var state = CreateState("ChatGPT-User/1.0");

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.First(c => c.ConfidenceDelta > 0);
        Assert.Equal(BotType.GoodBot.ToString(), botContrib.BotType);
    }
}
