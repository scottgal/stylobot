using System.Text.Json;

namespace Mostlylucid.BotDetection.Benchmarks.Harness;

public static class RegressionChecker
{
    public static int Check(string resultsDir, IReadOnlyList<BenchmarkScenario> scenarios)
    {
        var thresholdScenarios = scenarios.Where(s => s.Thresholds != null).ToList();
        if (thresholdScenarios.Count == 0)
        {
            Console.WriteLine("No scenarios with thresholds defined.");
            return 0;
        }

        var violations = new List<string>();

        if (!Directory.Exists(resultsDir))
        {
            Console.Error.WriteLine($"Results directory not found: {resultsDir}");
            return 1;
        }

        var jsonFiles = Directory.GetFiles(resultsDir, "*-report-full.json", SearchOption.AllDirectories);
        if (jsonFiles.Length == 0)
        {
            Console.Error.WriteLine("No BenchmarkDotNet result files found.");
            return 1;
        }

        foreach (var jsonFile in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(jsonFile);
                using var doc = JsonDocument.Parse(json);
                var benchmarks = doc.RootElement.GetProperty("Benchmarks");

                foreach (var benchmark in benchmarks.EnumerateArray())
                {
                    var displayInfo = benchmark.GetProperty("DisplayInfo").GetString() ?? "";

                    foreach (var scenario in thresholdScenarios)
                    {
                        if (!displayInfo.Contains(scenario.Name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var stats = benchmark.GetProperty("Statistics");
                        var meanNs = stats.GetProperty("Mean").GetDouble();

                        if (scenario.Thresholds!.MaxMeanNs.HasValue && meanNs > scenario.Thresholds.MaxMeanNs.Value)
                            violations.Add(
                                $"FAIL: {scenario.Name} mean {meanNs:F0}ns > threshold {scenario.Thresholds.MaxMeanNs}ns");

                        if (scenario.Thresholds.MaxP95Ns.HasValue)
                        {
                            var p95 = stats.GetProperty("Percentiles").GetProperty("P95").GetDouble();
                            if (p95 > scenario.Thresholds.MaxP95Ns.Value)
                                violations.Add(
                                    $"FAIL: {scenario.Name} P95 {p95:F0}ns > threshold {scenario.Thresholds.MaxP95Ns}ns");
                        }

                        if (scenario.Thresholds.MaxAllocatedBytes.HasValue &&
                            benchmark.TryGetProperty("Memory", out var mem))
                        {
                            var allocated = mem.GetProperty("BytesAllocatedPerOperation").GetInt64();
                            if (allocated > scenario.Thresholds.MaxAllocatedBytes.Value)
                                violations.Add(
                                    $"FAIL: {scenario.Name} allocated {allocated}B > threshold {scenario.Thresholds.MaxAllocatedBytes}B");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error parsing {jsonFile}: {ex.Message}");
            }
        }

        if (violations.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n{violations.Count} regression(s) detected:");
            foreach (var v in violations)
                Console.WriteLine($"  {v}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nAll {thresholdScenarios.Count} threshold checks passed.");
        Console.ResetColor();
        return 0;
    }
}
