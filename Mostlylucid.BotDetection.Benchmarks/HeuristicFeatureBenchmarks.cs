using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for HeuristicFeatureExtractor — the highest-allocated path in the heuristic detector.
///     Run: cd Mostlylucid.BotDetection.Benchmarks && dotnet run -c Release -- --filter *HeuristicFeature*
/// </summary>
[Config(typeof(HeuristicConfig))]
public class HeuristicFeatureBenchmarks
{
    private sealed class HeuristicConfig : ManualConfig
    {
        public HeuristicConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(3)
                .WithIterationCount(10));
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    private HttpContext _context = null!;
    private AggregatedEvidence _evidence10 = null!;
    private AggregatedEvidence _evidence30 = null!;
    private AggregatedEvidence _evidenceNoContribs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new DefaultHttpContext();
        _context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36";
        _context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        _context.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        _context.Request.Headers.AcceptEncoding = "gzip, deflate, br";

        var detectorNames = new[]
        {
            "UserAgent", "Header", "Ip", "Behavioral", "Heuristic",
            "CacheBehavior", "CookieBehavior", "Inconsistency", "AiScraper", "Haxxor"
        };
        var categories = new[] { "Automated", "Browser", "Network", "Behavioral", "Timing" };

        _evidence10 = BuildEvidence(10, detectorNames, categories);
        _evidence30 = BuildEvidence(30, detectorNames, categories);
        _evidenceNoContribs = BuildEvidence(0, detectorNames, categories);
    }

    [Benchmark(Description = "ExtractFeatures — 10 contributions")]
    public Dictionary<string, float> Extract10() =>
        HeuristicFeatureExtractor.ExtractFeatures(_context, _evidence10);

    [Benchmark(Description = "ExtractFeatures — 30 contributions")]
    public Dictionary<string, float> Extract30() =>
        HeuristicFeatureExtractor.ExtractFeatures(_context, _evidence30);

    [Benchmark(Description = "ExtractFeatures — no contributions")]
    public Dictionary<string, float> ExtractEmpty() =>
        HeuristicFeatureExtractor.ExtractFeatures(_context, _evidenceNoContribs);

    private static AggregatedEvidence BuildEvidence(
        int contributionCount,
        string[] detectorNames,
        string[] categories)
    {
        var rng = new Random(42);
        var contributions = new List<DetectionContribution>(contributionCount);
        for (var i = 0; i < contributionCount; i++)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = detectorNames[i % detectorNames.Length],
                Category = categories[i % categories.Length],
                ConfidenceDelta = rng.NextDouble() * 2 - 1,
                Weight = 1.0,
                Reason = $"Reason {i}"
            });
        }

        var ledger = new DetectionLedger("bench-request");
        foreach (var c in contributions)
            ledger.AddContribution(c);

        var catBreakdown = new Dictionary<string, CategoryScore>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in categories)
        {
            catBreakdown[cat] = new CategoryScore
            {
                Score = rng.NextDouble(),
                ContributionCount = rng.Next(1, 5)
            };
        }

        var signals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["transport.protocol_class"] = "document",
            ["request.is_datacenter"] = false,
            ["ua.is_known_browser"] = true,
            ["cache.has_cookies"] = true
        };

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = rng.NextDouble(),
            Confidence = rng.NextDouble(),
            RiskBand = RiskBand.Low,
            CategoryBreakdown = catBreakdown,
            Signals = signals,
            ContributingDetectors = new HashSet<string>(detectorNames),
            FailedDetectors = new HashSet<string>(),
            TotalProcessingTimeMs = rng.NextDouble() * 50
        };
    }
}
