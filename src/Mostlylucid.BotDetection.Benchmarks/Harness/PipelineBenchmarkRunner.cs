using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class PipelineBenchmarkRunner
{
    private BlackboardOrchestrator _orchestrator = null!;
    private HttpContext _httpContext = null!;

    [ParamsSource(nameof(Scenarios))]
    public BenchmarkScenario Scenario { get; set; } = null!;

    public static IEnumerable<BenchmarkScenario> Scenarios
    {
        get
        {
            var dir = DetectorBenchmarkRunner.FindScenariosDir();
            return BenchmarkScenarioLoader.LoadAll(dir)
                .Where(s => s.IsPipeline);
        }
    }

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
        services.AddBotDetection();

        var provider = services.BuildServiceProvider();
        _orchestrator = provider.GetRequiredService<BlackboardOrchestrator>();
        _httpContext = Scenario.BuildHttpContext();
    }

    [Benchmark]
    public Task<AggregatedEvidence> DetectPipeline()
        => _orchestrator.DetectAsync(_httpContext);
}
