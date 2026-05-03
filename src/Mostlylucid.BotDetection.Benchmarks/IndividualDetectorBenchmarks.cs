using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.SimulationPacks;
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
    private AiScraperContributor _aiScraperDetector = null!;
    private BlackboardState _aiScraperState = null!;
    private BehavioralContributor _behavioralDetector = null!;
    private BlackboardState _botState = null!;
    private HeaderContributor _headerDetector = null!;
    private HeuristicContributor _heuristicDetector = null!;
    private Http3FingerprintContributor _http3Detector = null!;
    private BlackboardState _http3State = null!;
    private BlackboardState _humanState = null!;
    private IpContributor _ipDetector = null!;
    private IServiceProvider _serviceProvider = null!;

    // Individual detectors
    private UserAgentContributor _userAgentDetector = null!;

    // Attack detectors
    private HaxxorContributor _haxxorDetector = null!;
    private BlackboardState _haxxorCleanState = null!;
    private BlackboardState _haxxorSqliState = null!;
    private BlackboardState _haxxorPathProbeState = null!;

    // ATO detectors
    private AccountTakeoverContributor _atoDetector = null!;
    private BlackboardState _atoCleanState = null!;
    private BlackboardState _atoLoginState = null!;

    // New identity + security detectors
    private SignatureContributor _signatureDetector = null!;
    private PeriodicityContributor _periodicityDetector = null!;
    private CveProbeContributor _cveProbeDetector = null!;
    private PiiQueryStringContributor _piiDetector = null!;
    private BlackboardState _cveProbeState = null!;
    private BlackboardState _piiState = null!;

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

        // Get detectors - all registered as IContributingDetector, resolve by type
        var allDetectors = _serviceProvider.GetServices<IContributingDetector>().ToList();
        _userAgentDetector = allDetectors.OfType<UserAgentContributor>().First();
        _ipDetector = allDetectors.OfType<IpContributor>().First();
        _headerDetector = allDetectors.OfType<HeaderContributor>().First();
        _behavioralDetector = allDetectors.OfType<BehavioralContributor>().First();
        _heuristicDetector = allDetectors.OfType<HeuristicContributor>().First();
        _http3Detector = allDetectors.OfType<Http3FingerprintContributor>().First();
        _aiScraperDetector = allDetectors.OfType<AiScraperContributor>().First();
        _haxxorDetector = allDetectors.OfType<HaxxorContributor>().First();
        _atoDetector = allDetectors.OfType<AccountTakeoverContributor>().First();
        _signatureDetector = allDetectors.OfType<SignatureContributor>().FirstOrDefault()!;
        _periodicityDetector = allDetectors.OfType<PeriodicityContributor>().FirstOrDefault()!;
        _cveProbeDetector = allDetectors.OfType<CveProbeContributor>().FirstOrDefault()!;
        _piiDetector = allDetectors.OfType<PiiQueryStringContributor>().FirstOrDefault()!;

        Console.WriteLine($"Resolved {allDetectors.Count} detectors: {string.Join(", ", allDetectors.Select(d => d.Name))}");
        Console.WriteLine($"Signature={_signatureDetector != null}, Periodicity={_periodicityDetector != null}, CveProbe={_cveProbeDetector != null}, PII={_piiDetector != null}");

        // Setup test states
        _humanState = CreateHumanState();
        _botState = CreateBotState();
        _http3State = CreateHttp3State();
        _aiScraperState = CreateAiScraperState();
        _haxxorCleanState = CreateHaxxorCleanState();
        _haxxorSqliState = CreateHaxxorSqliState();
        _haxxorPathProbeState = CreateHaxxorPathProbeState();
        _atoCleanState = CreateAtoCleanState();
        _atoLoginState = CreateAtoLoginState();
        _cveProbeState = CreateCveProbeState();
        _piiState = CreatePiiState();
    }

    // =========================================
    // Benchmark methods - one per detector × scenario
    // =========================================

    // --- Core detectors ---

    [Benchmark(Description = "UserAgent: human Chrome")]
    public Task UserAgent_Human() => _userAgentDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "UserAgent: curl bot")]
    public Task UserAgent_Bot() => _userAgentDetector.ContributeAsync(_botState);

    [Benchmark(Description = "Header: human with full headers")]
    public Task Header_Human() => _headerDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "Header: bot with minimal headers")]
    public Task Header_Bot() => _headerDetector.ContributeAsync(_botState);

    [Benchmark(Description = "IP: public IP")]
    public Task Ip_Public() => _ipDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "Behavioral: human")]
    public Task Behavioral_Human() => _behavioralDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "Heuristic: human")]
    public Task Heuristic_Human() => _heuristicDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "Heuristic: bot")]
    public Task Heuristic_Bot() => _heuristicDetector.ContributeAsync(_botState);

    // --- Fingerprint detectors ---

    [Benchmark(Description = "HTTP/3: QUIC fingerprint")]
    public Task Http3_Fingerprint() => _http3Detector.ContributeAsync(_http3State);

    [Benchmark(Description = "AI Scraper: detection")]
    public Task AiScraper_Detection() => _aiScraperDetector.ContributeAsync(_aiScraperState);

    // --- Attack detectors ---

    [Benchmark(Description = "Haxxor: clean request")]
    public Task Haxxor_Clean() => _haxxorDetector.ContributeAsync(_haxxorCleanState);

    [Benchmark(Description = "Haxxor: SQL injection")]
    public Task Haxxor_SqlInjection() => _haxxorDetector.ContributeAsync(_haxxorSqliState);

    [Benchmark(Description = "Haxxor: path probe")]
    public Task Haxxor_PathProbe() => _haxxorDetector.ContributeAsync(_haxxorPathProbeState);

    // --- Identity detectors ---

    [Benchmark(Description = "AccountTakeover: clean GET")]
    public Task ATO_Clean() => _atoDetector.ContributeAsync(_atoCleanState);

    [Benchmark(Description = "AccountTakeover: login POST")]
    public Task ATO_Login() => _atoDetector.ContributeAsync(_atoLoginState);

    // --- NEW detectors (this session) ---

    [Benchmark(Description = "Signature: compute PrimarySignature + header hashes")]
    public Task Signature_Compute() => _signatureDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "Periodicity: timing analysis")]
    public Task Periodicity_Analyze() => _periodicityDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "CveProbe: WordPress path")]
    public Task CveProbe_WordPress() => _cveProbeDetector.ContributeAsync(_cveProbeState);

    [Benchmark(Description = "CveProbe: clean path (no match)")]
    public Task CveProbe_Clean() => _cveProbeDetector.ContributeAsync(_humanState);

    [Benchmark(Description = "PII: query with email")]
    public Task Pii_WithEmail() => _piiDetector.ContributeAsync(_piiState);

    [Benchmark(Description = "PII: clean query")]
    public Task Pii_Clean() => _piiDetector.ContributeAsync(_humanState);

    private static BlackboardState CreateState(
        DefaultHttpContext context,
        Dictionary<string, object>? signals = null,
        string requestId = "bench")
    {
        var signalDict = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(
            signals ?? new Dictionary<string, object>());

        return new BlackboardState
        {
            HttpContext = context,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0.0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = requestId,
            Elapsed = TimeSpan.Zero
        };
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

        return CreateState(context, requestId: "bench-human");
    }

    private BlackboardState CreateBotState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "curl/8.4.0";
        context.Request.Headers.Accept = "*/*";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.50");
        context.Request.Method = "GET";
        context.Request.Path = "/api/data";

        return CreateState(context, requestId: "bench-bot");
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

    [Benchmark(Description = "HTTP/3 Fingerprint Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> Http3FingerprintDetector()
    {
        return await _http3Detector.ContributeAsync(_http3State, CancellationToken.None);
    }

    [Benchmark(Description = "AI Scraper Detector")]
    public async Task<IReadOnlyList<DetectionContribution>> AiScraperDetector()
    {
        return await _aiScraperDetector.ContributeAsync(_aiScraperState, CancellationToken.None);
    }

    private BlackboardState CreateHttp3State()
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/3";
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        context.Request.Headers["X-QUIC-Transport-Params"] = "initial_max_data=15728640";
        context.Request.Headers["X-QUIC-Version"] = "v1";
        context.Request.Headers["X-QUIC-0RTT"] = "1";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.Request.Method = "GET";
        context.Request.Path = "/";

        return CreateState(context, requestId: "bench-http3");
    }

    private BlackboardState CreateAiScraperState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)";
        context.Request.Headers.Accept = "text/markdown, text/html;q=0.9";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.10");
        context.Request.Method = "GET";
        context.Request.Path = "/";

        return CreateState(context, requestId: "bench-ai-scraper");
    }

    // ===== Haxxor Benchmarks =====

    [Benchmark(Description = "Haxxor: Clean Path (zero alloc)")]
    public async Task<IReadOnlyList<DetectionContribution>> HaxxorCleanPath()
    {
        return await _haxxorDetector.ContributeAsync(_haxxorCleanState, CancellationToken.None);
    }

    [Benchmark(Description = "Haxxor: SQLi Attack")]
    public async Task<IReadOnlyList<DetectionContribution>> HaxxorSqliAttack()
    {
        return await _haxxorDetector.ContributeAsync(_haxxorSqliState, CancellationToken.None);
    }

    [Benchmark(Description = "Haxxor: Path Probe (/wp-admin)")]
    public async Task<IReadOnlyList<DetectionContribution>> HaxxorPathProbe()
    {
        return await _haxxorDetector.ContributeAsync(_haxxorPathProbeState, CancellationToken.None);
    }

    // ===== ATO Benchmarks =====

    [Benchmark(Description = "ATO: Clean Path (zero alloc)")]
    public async Task<IReadOnlyList<DetectionContribution>> AtoCleanPath()
    {
        return await _atoDetector.ContributeAsync(_atoCleanState, CancellationToken.None);
    }

    [Benchmark(Description = "ATO: Login POST")]
    public async Task<IReadOnlyList<DetectionContribution>> AtoLoginPost()
    {
        return await _atoDetector.ContributeAsync(_atoLoginState, CancellationToken.None);
    }

    // ===== State creation helpers =====

    /// <summary>Normal GET to / - should be zero-allocation fast path.</summary>
    private BlackboardState CreateHaxxorCleanState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.Request.Method = "GET";
        context.Request.Path = "/products/widget-123";
        context.Request.QueryString = new QueryString("?color=blue&size=large");

        return CreateState(context, requestId: "bench-haxxor-clean");
    }

    /// <summary>SQL injection in query string - should trigger detection.</summary>
    private BlackboardState CreateHaxxorSqliState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "curl/8.4.0";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.50");
        context.Request.Method = "GET";
        context.Request.Path = "/search";
        context.Request.QueryString = new QueryString("?q=1' UNION SELECT username,password FROM users--");

        return CreateState(context, requestId: "bench-haxxor-sqli");
    }

    /// <summary>WordPress admin probe - should trigger path probe detection.</summary>
    private BlackboardState CreateHaxxorPathProbeState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "python-requests/2.31.0";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.60");
        context.Request.Method = "GET";
        context.Request.Path = "/wp-admin/install.php";

        return CreateState(context, requestId: "bench-haxxor-probe");
    }

    /// <summary>WordPress probe path - should trigger CVE probe detection.</summary>
    private BlackboardState CreateCveProbeState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "python-requests/2.31.0";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.80");
        context.Request.Method = "GET";
        context.Request.Path = "/wp-login.php";

        return CreateState(context, new Dictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = "sig_benchmark_cve"
        }, requestId: "bench-cve-probe");
    }

    /// <summary>Query string with PII - should trigger PII detection.</summary>
    private BlackboardState CreatePiiState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.Request.Method = "GET";
        context.Request.Path = "/search";
        context.Request.QueryString = new QueryString("?email=user@example.com&token=abc123def456ghi789jkl012mno345");

        return CreateState(context, new Dictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = "sig_benchmark_pii"
        }, requestId: "bench-pii");
    }

    /// <summary>Normal GET - no waveform signature signal, returns immediately.</summary>
    private BlackboardState CreateAtoCleanState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.Request.Method = "GET";
        context.Request.Path = "/dashboard";

        return CreateState(context, new Dictionary<string, object>
        {
            [Models.SignalKeys.UserAgentFamily] = "Chrome",
            [Models.SignalKeys.PrimarySignature] = "sig_benchmark_clean"
        }, requestId: "bench-ato-clean");
    }

    /// <summary>POST to /login - triggers login tracking.</summary>
    private BlackboardState CreateAtoLoginState()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "python-requests/2.31.0";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.70");
        context.Request.Method = "POST";
        context.Request.Path = "/login";

        return CreateState(context, new Dictionary<string, object>
        {
            [Models.SignalKeys.UserAgentFamily] = "Python",
            [Models.SignalKeys.PrimarySignature] = "sig_benchmark_login"
        }, requestId: "bench-ato-login");
    }
}