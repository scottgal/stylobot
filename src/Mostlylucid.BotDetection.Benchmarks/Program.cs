using BenchmarkDotNet.Running;
using Mostlylucid.BotDetection.Benchmarks.Harness;

namespace Mostlylucid.BotDetection.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--list-scenarios"))
        {
            var scenarios = BenchmarkScenarioLoader.LoadAll(FindScenariosDir());
            Console.WriteLine($"Found {scenarios.Count} benchmark scenarios:\n");
            foreach (var group in scenarios.GroupBy(s => s.DetectorName))
            {
                Console.WriteLine($"  {group.Key}:");
                foreach (var s in group)
                {
                    var tags = s.Tags != null ? $" [{string.Join(", ", s.Tags)}]" : "";
                    var thresh = s.Thresholds != null ? " (has thresholds)" : "";
                    Console.WriteLine($"    - {s.Name}{tags}{thresh}");
                }
            }
            return 0;
        }

        if (args.Contains("--regression"))
        {
            var cleanArgs = args.Where(a => a != "--regression").ToArray();
            BenchmarkRunner.Run<DetectorBenchmarkRunner>(null, cleanArgs);
            BenchmarkRunner.Run<PipelineBenchmarkRunner>(null, cleanArgs);

            var scenarios = BenchmarkScenarioLoader.LoadAll(FindScenariosDir());
            return RegressionChecker.Check("BenchmarkDotNet.Artifacts/results", scenarios);
        }

        // Default: interactive BenchmarkDotNet mode (includes old benchmarks + new YAML-driven ones)
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    private static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "Scenarios");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        // Fallback: relative to current directory
        return Path.Combine(Directory.GetCurrentDirectory(), "Scenarios");
    }
}
