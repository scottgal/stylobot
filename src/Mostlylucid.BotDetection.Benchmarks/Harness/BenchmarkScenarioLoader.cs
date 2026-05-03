using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

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
}
