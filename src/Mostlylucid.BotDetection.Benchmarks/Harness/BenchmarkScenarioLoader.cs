using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

/// <summary>
///     Loads <c>*.benchmark.yaml</c> scenarios from the <c>Scenarios/</c> directory. Recovered
///     from the pre-atom-refactor harness (deleted in <c>cbf0c564</c>); <see cref="FindScenariosDir"/>
///     was folded in from the deleted <c>DetectorBenchmarkRunner</c> so the pipeline runner can
///     locate the directory both in-host (discovery) and inside a BenchmarkDotNet-generated
///     project (the scenarios are copied to output via the csproj, so the upward walk finds them).
/// </summary>
public static class BenchmarkScenarioLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<BenchmarkScenario> LoadAll(string scenarioDir)
    {
        var dir = Path.GetFullPath(scenarioDir);
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Scenario directory not found: {dir}");
            return [];
        }

        var files = Directory.GetFiles(dir, "*.benchmark.yaml", SearchOption.AllDirectories);
        var scenarios = new List<BenchmarkScenario>();

        foreach (var file in files.OrderBy(f => f))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var scenario = Deserializer.Deserialize<BenchmarkScenario>(yaml);
                if (string.IsNullOrWhiteSpace(scenario.Name))
                    scenario.Name = Path.GetFileNameWithoutExtension(file);
                scenarios.Add(scenario);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load {file}: {ex.Message}");
            }
        }

        return scenarios
            .OrderBy(s => s.DetectorName)
            .ThenBy(s => s.Name)
            .ToList();
    }

    public static IReadOnlyList<BenchmarkScenario> LoadByDetector(string scenarioDir, string detectorName)
        => LoadAll(scenarioDir).Where(s => s.DetectorName == detectorName).ToList();

    public static IReadOnlyList<BenchmarkScenario> LoadByTag(string scenarioDir, string tag)
        => LoadAll(scenarioDir).Where(s => s.Tags?.Contains(tag) == true).ToList();

    /// <summary>
    ///     Walks up from the running assembly's base directory to find the <c>Scenarios/</c>
    ///     folder. Robust both in-host and inside a BenchmarkDotNet-generated project (nested
    ///     under the benchmark's output dir), since the scenarios are copied to output.
    /// </summary>
    public static string FindScenariosDir()
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
