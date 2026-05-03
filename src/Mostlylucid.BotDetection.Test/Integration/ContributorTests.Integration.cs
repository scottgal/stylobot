using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Integration;

/// <summary>
///     Integration tests to verify all contributors run and always report results.
/// </summary>
[Trait("Category", "Integration")]
public class ContributorIntegrationTests
{
    private readonly IServiceProvider _sp;

    public ContributorIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:EnableUserAgentDetection"] = "true",
                ["BotDetection:EnableHeaderAnalysis"] = "true",
                ["BotDetection:EnableIpDetection"] = "true",
                ["BotDetection:EnableBehavioralAnalysis"] = "true",
                ["BotDetection:EnableLlmDetection"] = "false",
                ["BotDetection:EnableTestMode"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddBotDetection();
        _sp = services.BuildServiceProvider();
    }

    [Fact]
    public void AllContributors_ShouldBeRegistered()
    {
        var contributors = _sp.GetServices<IContributingDetector>().Select(c => c.Name).ToList();
        Assert.Contains("FastPathReputation", contributors);
        Assert.Contains("UserAgent", contributors);
        Assert.Contains("Header", contributors);
        Assert.Contains("Ip", contributors);
        Assert.Contains("SecurityTool", contributors);
        Assert.Contains("ProjectHoneypot", contributors);
        Assert.Contains("Behavioral", contributors);
        Assert.Contains("ClientSide", contributors);
        Assert.Contains("Inconsistency", contributors);
        Assert.Contains("VersionAge", contributors);
        Assert.Contains("ReputationBias", contributors);
        Assert.Contains("Heuristic", contributors);
    }

    [Fact]
    public async Task FastPathReputationContributor_AlwaysContributes()
    {
        var c = GetContributor("FastPathReputation");
        var result = await c.ContributeAsync(CreateState("Mozilla/5.0 Chrome/120", "192.168.1.1"));
        Assert.NotEmpty(result);
        Assert.Contains(result, r => r.DetectorName == "FastPathReputation");
    }

    [Fact]
    public async Task SecurityToolContributor_AlwaysContributes()
    {
        var c = GetContributor("SecurityTool");
        var result = await c.ContributeAsync(CreateState("Mozilla/5.0 Chrome/120", "192.168.1.1"));
        Assert.NotEmpty(result);
        Assert.Contains(result, r => r.DetectorName == "SecurityTool");
    }

    [Fact]
    public async Task SecurityToolContributor_DetectsSqlmap()
    {
        var c = GetContributor("SecurityTool");
        var result = await c.ContributeAsync(CreateState("sqlmap/1.5.2#stable", "192.168.1.1"));
        Assert.NotEmpty(result);
        Assert.True(result.First().ConfidenceDelta > 0.5);
    }

    [Fact]
    public async Task ProjectHoneypotContributor_ContributesForLocalhost()
    {
        var c = GetContributor("ProjectHoneypot");
        var signals = new Dictionary<string, object>
        {
            ["ip.is_local"] = true,
            ["ip.address"] = "127.0.0.1"
        };
        var result = await c.ContributeAsync(CreateState("Mozilla/5.0", "127.0.0.1", signals));
        Assert.NotEmpty(result);
        Assert.Contains(result, r => r.DetectorName == "ProjectHoneypot");
    }

    [Fact]
    public async Task ReputationBiasContributor_AlwaysContributes()
    {
        var c = GetContributor("ReputationBias");
        var result = await c.ContributeAsync(CreateState("Mozilla/5.0 Chrome/120", "192.168.1.1"));
        Assert.NotEmpty(result);
        Assert.Contains(result, r => r.DetectorName == "ReputationBias");
    }

    [Fact]
    public async Task HeuristicContributor_ClassifiesNormalBrowserAsHuman()
    {
        var c = GetContributor("Heuristic");
        var signals = new Dictionary<string, object>
        {
            ["header.count"] = 15,
            ["header.has_accept"] = true,
            ["header.has_accept_language"] = true,
            ["header.has_accept_encoding"] = true
        };
        // A real browser sends Accept-Language, Accept, Referer etc.
        var headers = new Dictionary<string, string>
        {
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            ["Accept-Encoding"] = "gzip, deflate, br"
        };
        var result = await c.ContributeAsync(CreateState(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
            "192.168.1.1", signals, headers));
        Assert.NotEmpty(result);
        Assert.True(result.First().ConfidenceDelta < 0, "Browser should have human signal");
    }

    [Fact]
    public async Task HeuristicContributor_ClassifiesCurlAsBot()
    {
        var c = GetContributor("Heuristic");
        var signals = new Dictionary<string, object>
        {
            ["header.count"] = 3,
            ["header.has_accept"] = true,
            ["header.has_accept_language"] = false,
            ["header.has_accept_encoding"] = false
        };
        var result = await c.ContributeAsync(CreateState("curl/7.68.0", "192.168.1.1", signals));
        Assert.NotEmpty(result);
        Assert.True(result.First().ConfidenceDelta > 0, "curl should have bot signal");
    }

    [Theory]
    [InlineData("<test-honeypot:harvester>", "Harvester", 75)]
    [InlineData("<test-honeypot:spammer>", "CommentSpammer", 100)]
    [InlineData("<test-honeypot:suspicious>", "Suspicious", 35)]
    public async Task ProjectHoneypotContributor_SimulatesHoneypotFromTestMarker(
        string testUserAgent, string expectedType, int expectedThreatScore)
    {
        var c = GetContributor("ProjectHoneypot");
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = testUserAgent;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        var signalDict = new ConcurrentDictionary<string, object>(new Dictionary<string, object>
        {
            [SignalKeys.ClientIp] = "192.168.1.1",
            [SignalKeys.IpIsLocal] = false
        });
        var state = new BlackboardState
        {
            HttpContext = ctx,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };

        var result = await c.ContributeAsync(state);
        Assert.NotEmpty(result);

        var contribution = result.First();
        Assert.Equal("ProjectHoneypot", contribution.DetectorName);
        Assert.True(contribution.ConfidenceDelta > 0, "Honeypot hit should increase bot probability");
        Assert.Contains("[TEST MODE]", contribution.Reason);
        Assert.Contains(expectedType, contribution.Reason);

        // Verify signals written to shared state contain test mode marker
        Assert.True(state.Signals.ContainsKey("HoneypotTestMode"));
        Assert.Equal(true, state.Signals["HoneypotTestMode"]);
        Assert.Equal(expectedThreatScore, state.Signals[SignalKeys.HoneypotThreatScore]);
    }

    [Fact]
    public async Task HeuristicLateContributor_IncorporatesAiSignals()
    {
        var c = GetContributor("HeuristicLate");

        // Create a state with AI signals indicating "human" classification
        var aiSignals = new Dictionary<string, object>
        {
            [SignalKeys.AiPrediction] = "human",
            [SignalKeys.AiConfidence] = 0.95,
            [SignalKeys.UserAgent] = "Mozilla/5.0 Chrome/120"
        };

        // Create LLM contribution to simulate AI ran
        var llmContribution = new DetectionContribution
        {
            DetectorName = "Llm",
            Category = "AI",
            ConfidenceDelta = -0.95, // negative = human
            Weight = 2.0,
            Reason = "LLM classified as human",
            Signals = aiSignals.ToImmutableDictionary()
        };

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0";
        ctx.Request.Headers.Accept = "text/html";
        ctx.Request.Headers.AcceptLanguage = "en-US";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");

        var signalDict2 = new ConcurrentDictionary<string, object>(aiSignals);
        var state = new BlackboardState
        {
            HttpContext = ctx,
            Signals = signalDict2,
            SignalWriter = signalDict2,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet.Create("Llm"),
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList.Create(llmContribution),
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.FromMilliseconds(500)
        };

        var result = await c.ContributeAsync(state);
        Assert.NotEmpty(result);
        Assert.Contains(result, r => r.DetectorName == "HeuristicLate");
    }

    /// <summary>
    ///     Verifies that production-recommended defaults are secure
    ///     (test mode disabled, response headers disabled, etc.)
    /// </summary>
    [Fact]
    public void ProductionDefaults_ShouldBeSecure()
    {
        // Create options with NO configuration (pure defaults)
        var options = new BotDetectionOptions();

        // Test mode MUST be disabled by default
        Assert.False(options.EnableTestMode, "Test mode must be disabled by default in production");

        // Response headers MUST be disabled by default (don't leak detection info to clients)
        Assert.False(options.ResponseHeaders.Enabled, "Response headers must be disabled by default");

        // Even if enabled, IncludeFullJson should be false
        Assert.False(options.ResponseHeaders.IncludeFullJson, "Full JSON should never be exposed by default");

        // Detection should be enabled by default
        Assert.True(options.EnableUserAgentDetection, "UA detection should be enabled by default");
        Assert.True(options.EnableHeaderAnalysis, "Header analysis should be enabled by default");
        Assert.True(options.EnableIpDetection, "IP detection should be enabled by default");
        Assert.True(options.EnableBehavioralAnalysis, "Behavioral analysis should be enabled by default");

        // Security tools detection should be enabled by default
        Assert.True(options.SecurityTools.Enabled, "Security tool detection should be enabled by default");
        Assert.True(options.SecurityTools.BlockSecurityTools, "Blocking security tools should be enabled by default");

        // Threshold should be reasonable (0.7 is typical)
        Assert.InRange(options.BotThreshold, 0.5, 0.9);
    }

    [Fact]
    public void ProductionDefaults_ProjectHoneypot_ShouldBeConfiguredSecurely()
    {
        var options = new BotDetectionOptions();

        // Honeypot should be disabled by default (requires API key)
        Assert.False(options.ProjectHoneypot.Enabled || !string.IsNullOrEmpty(options.ProjectHoneypot.AccessKey),
            "Honeypot should not be enabled without an access key");

        // Skip local IPs should be true
        Assert.True(options.ProjectHoneypot.SkipLocalIps, "Should skip localhost by default");

        // Threat threshold should be reasonable
        Assert.InRange(options.ProjectHoneypot.HighThreatThreshold, 10, 50);
    }

    private IContributingDetector GetContributor(string name)
    {
        return _sp.GetServices<IContributingDetector>().First(c => c.Name == name);
    }

    private static BlackboardState CreateState(string ua, string ip, Dictionary<string, object>? signals = null,
        Dictionary<string, string>? headers = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = ua;
        ctx.Connection.RemoteIpAddress = IPAddress.TryParse(ip, out var addr) ? addr : null;

        if (headers != null)
            foreach (var (key, value) in headers)
                ctx.Request.Headers[key] = value;

        var signalDict = new ConcurrentDictionary<string, object>(signals ?? new Dictionary<string, object>());
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }
}