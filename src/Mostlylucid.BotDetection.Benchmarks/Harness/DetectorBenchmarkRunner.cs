using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class DetectorBenchmarkRunner
{
    private IContributingDetector _detector = null!;
    private BlackboardState _state = null!;

    [ParamsSource(nameof(Scenarios))]
    public BenchmarkScenario Scenario { get; set; } = null!;

    public static IEnumerable<BenchmarkScenario> Scenarios
    {
        get
        {
            var dir = FindScenariosDir();
            return BenchmarkScenarioLoader.LoadAll(dir)
                .Where(s => !s.IsPipeline);
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
        var allDetectors = provider.GetServices<IContributingDetector>().ToList();

        _detector = allDetectors.FirstOrDefault(d =>
                        d.Name.Equals(Scenario.DetectorName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Detector '{Scenario.DetectorName}' not found. Available: {string.Join(", ", allDetectors.Select(d => d.Name))}");

        _state = Scenario.ToBlackboardState();
    }

    [Benchmark]
    public Task<IReadOnlyList<DetectionContribution>> Detect()
        => _detector.ContributeAsync(_state);

    internal static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "Scenarios");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        return Path.Combine(AppContext.BaseDirectory, "Scenarios");
    }
}
