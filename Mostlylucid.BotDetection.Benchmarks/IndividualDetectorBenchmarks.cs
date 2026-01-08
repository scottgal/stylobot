using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for individual detectors to identify allocation hotspots.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class IndividualDetectorBenchmarks
{
    private BehavioralContributor _behavioralDetector = null!;
    private BlackboardState _botState = null!;
    private HeaderContributor _headerDetector = null!;
    private HeuristicContributor _heuristicDetector = null!;
    private BlackboardState _humanState = null!;
    private IpContributor _ipDetector = null!;
    private IServiceProvider _serviceProvider = null!;

    // Individual detectors
    private UserAgentContributor _userAgentDetector = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Configure minimal configuration
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Enabled"] = "true",
                ["BotDetection:AiDetection:OllamaEnabled"] = "false",
                ["BotDetection:AiDetection:AnthropicEnabled"] = "false"
            })
            .Build();

        // Create service collection
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddBotDetection();

        _serviceProvider = services.BuildServiceProvider();

        // Get detectors
        _userAgentDetector = _serviceProvider.GetRequiredService<UserAgentContributor>();
        _ipDetector = _serviceProvider.GetRequiredService<IpContributor>();
        _headerDetector = _serviceProvider.GetRequiredService<HeaderContributor>();
        _behavioralDetector = _serviceProvider.GetRequiredService<BehavioralContributor>();
        _heuristicDetector = _serviceProvider.GetRequiredService<HeuristicContributor>();

        // Setup test states
        _humanState = CreateHumanState();
        _botState = CreateBotState();
    }

    private BlackboardState CreateHumanState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        context.Request.Headers.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
        context.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        context.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        context.Request.Headers.Referer = "https://google.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.Request.Method = "GET";
        context.Request.Path = "/";

        return new BlackboardState
        {
            HttpContext = context,
            Signals = new Dictionary<string, object>(),
            CurrentRiskScore = 0.0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = "test-request",
            Elapsed = TimeSpan.Zero
        };
    }

    private BlackboardState CreateBotState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "curl/8.4.0";
        context.Request.Headers.Accept = "*/*";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.50");
        context.Request.Method = "GET";
        context.Request.Path = "/api/data";

        return new BlackboardState
        {
            HttpContext = context,
            Signals = new Dictionary<string, object>(),
            CurrentRiskScore = 0.0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = "test-request",
            Elapsed = TimeSpan.Zero
        };
    }

    [Benchmark(Description = "UserAgent Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> UserAgentDetector()
    {
        return await _userAgentDetector.ContributeAsync(_botState, CancellationToken.None);
    }

    [Benchmark(Description = "IP Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> IpDetector()
    {
        return await _ipDetector.ContributeAsync(_humanState, CancellationToken.None);
    }

    [Benchmark(Description = "Header Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> HeaderDetector()
    {
        return await _headerDetector.ContributeAsync(_humanState, CancellationToken.None);
    }

    [Benchmark(Description = "Behavioral Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> BehavioralDetector()
    {
        return await _behavioralDetector.ContributeAsync(_humanState, CancellationToken.None);
    }

    [Benchmark(Description = "Heuristic Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> HeuristicDetector()
    {
        return await _heuristicDetector.ContributeAsync(_humanState, CancellationToken.None);
    }
}