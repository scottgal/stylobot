using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for the bot detection request processing pipeline.
///     Tests various scenarios with predictable results (no AI/LLM randomness).
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class DetectionPipelineBenchmarks
{
    private HttpContext _botContext = null!;
    private HttpContext _datacenterContext = null!;
    private HttpContext _humanContext = null!;
    private BlackboardOrchestrator _orchestrator = null!;
    private HttpContext _searchBotContext = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Configure minimal configuration for predictable benchmarking (no AI)
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Enabled"] = "true",
                ["BotDetection:AiDetection:OllamaEnabled"] = "false",
                ["BotDetection:AiDetection:AnthropicEnabled"] = "false"
            })
            .Build();

        // Create service collection and use the built-in bot detection registration
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        // Use the built-in AddBotDetection extension
        services.AddBotDetection();

        var provider = services.BuildServiceProvider();

        // Get orchestrator from DI
        _orchestrator = provider.GetRequiredService<BlackboardOrchestrator>();

        // Setup test contexts
        _humanContext = CreateHumanContext();
        _botContext = CreateBotContext();
        _searchBotContext = CreateSearchBotContext();
        _datacenterContext = CreateDatacenterContext();
    }

    private HttpContext CreateHumanContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        context.Request.Headers.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
        context.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        context.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        context.Request.Headers.Referer = "https://google.com";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42"); // Non-datacenter IP
        context.Request.Method = "GET";
        context.Request.Path = "/";
        return context;
    }

    private HttpContext CreateBotContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "curl/8.4.0";
        context.Request.Headers.Accept = "*/*";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.50"); // Non-datacenter IP
        context.Request.Method = "GET";
        context.Request.Path = "/api/data";
        return context;
    }

    private HttpContext CreateSearchBotContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";
        context.Request.Headers.Accept = "text/html";
        context.Connection.RemoteIpAddress = IPAddress.Parse("66.249.64.1"); // Google IP
        context.Request.Method = "GET";
        context.Request.Path = "/";
        return context;
    }

    private HttpContext CreateDatacenterContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
        context.Request.Headers.Accept = "text/html";
        context.Connection.RemoteIpAddress = IPAddress.Parse("3.5.140.2"); // AWS IP
        context.Request.Method = "GET";
        context.Request.Path = "/";
        return context;
    }

    // ==========================================
    // Full Pipeline Benchmarks
    // ==========================================

    [Benchmark(Description = "Human request (typical website visitor)")]
    public async Task<AggregatedEvidence> DetectHuman()
    {
        return await _orchestrator.DetectAsync(_humanContext, CancellationToken.None);
    }

    [Benchmark(Description = "Obvious bot (curl user-agent)")]
    public async Task<AggregatedEvidence> DetectBot()
    {
        return await _orchestrator.DetectAsync(_botContext, CancellationToken.None);
    }

    [Benchmark(Description = "Search engine bot (Googlebot)")]
    public async Task<AggregatedEvidence> DetectSearchBot()
    {
        return await _orchestrator.DetectAsync(_searchBotContext, CancellationToken.None);
    }

    [Benchmark(Description = "Datacenter IP with browser UA")]
    public async Task<AggregatedEvidence> DetectDatacenterBot()
    {
        return await _orchestrator.DetectAsync(_datacenterContext, CancellationToken.None);
    }
}