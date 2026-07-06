using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;         // AggregatedEvidence
using Mostlylucid.BotDetection.Orchestration.Atoms;   // BotDetectionOrchestrator

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

/// <summary>
///     Full atom-pipeline benchmark: drives <see cref="BotDetectionOrchestrator"/> (the live
///     per-request detection path — see <c>BotDetectionMiddleware</c>) end to end for each
///     <c>detector: _pipeline</c> scenario (human Chrome, curl bot, GPTBot AI scraper).
///
///     <para>
///         Rebuilt after the atom refactor. The pre-refactor runner drove the deleted
///         <c>BlackboardOrchestrator</c> and reused a single instance; the atom orchestrator
///         carries a per-instance signal sink that accumulates across calls, so each op resolves
///         a fresh scoped orchestrator (exactly as the middleware does per request) and builds a
///         fresh context. The measured cost is therefore the faithful full per-request cost:
///         scope + orchestrator construction (atom registration) + hydration + detection.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class PipelineBenchmarkRunner
{
    private ServiceProvider _provider = null!;

    [ParamsSource(nameof(Scenarios))]
    public BenchmarkScenario Scenario { get; set; } = null!;

    public static IEnumerable<BenchmarkScenario> Scenarios =>
        BenchmarkScenarioLoader.LoadAll(BenchmarkScenarioLoader.FindScenariosDir())
            .Where(s => s.IsPipeline);

    [GlobalSetup]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Enabled"] = "true",
                ["BotDetection:AiDetection:OllamaEnabled"] = "false",
                ["BotDetection:AiDetection:AnthropicEnabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddBotDetection();
        _provider = services.BuildServiceProvider();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // Must be async: the provider holds services (e.g. ScheduleCoordinator) that only
        // implement IAsyncDisposable, so a sync Dispose() throws and BenchmarkDotNet then
        // invalidates the whole result to NA.
        if (_provider is not null)
            await _provider.DisposeAsync();
    }

    [Benchmark]
    public async Task<AggregatedEvidence> DetectPipeline()
    {
        using var scope = _provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<BotDetectionOrchestrator>();
        var httpContext = Scenario.BuildHttpContext();
        httpContext.RequestServices = scope.ServiceProvider; // mirror the middleware's request scope
        return await orchestrator.DetectAsync(httpContext);
    }
}
