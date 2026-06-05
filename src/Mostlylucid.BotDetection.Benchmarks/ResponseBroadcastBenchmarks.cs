using BenchmarkDotNet.Attributes;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for the dashboard broadcast layer's per-request aggregation work.
///     The dedup target: previously every detection event walked
///     <c>evidence.Contributions</c> six times via LINQ (Where + OrderByDescending +
///     Take + Select + ToList for top reasons, then GroupBy + ToDictionary with four
///     Sum() calls per group plus First() for Priority and a string.Join scanning the
///     group again for reasons), and PublishEventAsync ran the topReasons + GroupBy
///     chain a second time. The aggregator now does it in one pass.
///
///     Run: dotnet run --project Mostlylucid.BotDetection.Benchmarks -c Release -- --filter *ResponseBroadcast*
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ResponseBroadcastBenchmarks
{
    private IReadOnlyList<DetectionContribution> _smallContribs = null!;
    private IReadOnlyList<DetectionContribution> _typicalContribs = null!;
    private IReadOnlyList<DetectionContribution> _largeContribs = null!;
    private object _geoSample = null!;
    private System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, string?>?> _accessorCache = null!;
    private System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _propInfoCache = null!;

    public sealed class FakeGeo { public string CountryCode { get; set; } = "GB"; }

    [GlobalSetup]
    public void Setup()
    {
        _smallContribs = BuildContributions(5);
        _typicalContribs = BuildContributions(15);  // matches the BDF replay scenario surface
        _largeContribs = BuildContributions(40);

        _geoSample = new FakeGeo { CountryCode = "GB" };

        _accessorCache = new System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, string?>?>();
        var prop = typeof(FakeGeo).GetProperty("CountryCode")!;
        var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
        var cast = System.Linq.Expressions.Expression.Convert(param, typeof(FakeGeo));
        var access = System.Linq.Expressions.Expression.Property(cast, prop);
        var del = System.Linq.Expressions.Expression
            .Lambda<Func<object, string?>>(access, param)
            .Compile();
        _accessorCache[typeof(FakeGeo)] = del;

        _propInfoCache = new System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?>();
        _propInfoCache[typeof(FakeGeo)] = prop;
    }

    [Benchmark(Description = "Aggregate (5 contributions)")]
    public (List<string>, Dictionary<string, DashboardDetectorContribution>) Aggregate_Small()
        => DetectionBroadcastMiddleware.AggregateContributionsAndTopReasons(_smallContribs);

    [Benchmark(Description = "Aggregate (15 contributions — typical request)")]
    public (List<string>, Dictionary<string, DashboardDetectorContribution>) Aggregate_Typical()
        => DetectionBroadcastMiddleware.AggregateContributionsAndTopReasons(_typicalContribs);

    [Benchmark(Description = "Aggregate (40 contributions — heavy request)")]
    public (List<string>, Dictionary<string, DashboardDetectorContribution>) Aggregate_Large()
        => DetectionBroadcastMiddleware.AggregateContributionsAndTopReasons(_largeContribs);

    /// <summary>
    ///     LINQ baseline equivalent — exactly the pre-edit BuildDetectionFromEvidence
    ///     chain so the bench captures the apples-to-apples improvement. Stays inline
    ///     so it can't accidentally drift away from the historical implementation.
    /// </summary>
    [Benchmark(Description = "Compiled-delegate CountryCode accessor (per-request hit)")]
    public string? CountryCodeAccessor_Delegate()
    {
        // GetCountryCodeAccessor is private; we exercise the same per-request shape
        // by going through the public ConcurrentDictionary directly with a pre-warmed
        // delegate entry. The benchmark establishes that subsequent lookups are
        // dictionary + delegate dispatch, not a reflection invocation.
        var t = typeof(FakeGeo);
        if (_accessorCache.TryGetValue(t, out var del))
            return del?.Invoke(_geoSample);
        return null;
    }

    [Benchmark(Description = "Reflection PropertyInfo CountryCode (baseline)")]
    public string? CountryCodeAccessor_Reflection()
    {
        var t = typeof(FakeGeo);
        if (_propInfoCache.TryGetValue(t, out var pi) && pi is not null)
            return pi.GetValue(_geoSample) as string;
        return null;
    }

    [Benchmark(Description = "LINQ baseline aggregate (15 contributions)")]
    public (List<string>, Dictionary<string, DashboardDetectorContribution>) LinqBaseline_Typical()
    {
        var contribs = _typicalContribs;

        var topReasons = contribs
            .Where(c => !string.IsNullOrEmpty(c.Reason))
            .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
            .Take(5)
            .Select(c => c.Reason!)
            .ToList();

        var detectorContributions = contribs
            .GroupBy(c => c.DetectorName)
            .ToDictionary(
                g => g.Key,
                g => new DashboardDetectorContribution
                {
                    ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                    Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                    Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                    ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                    Priority = g.First().Priority
                });

        return (topReasons, detectorContributions);
    }

    private static IReadOnlyList<DetectionContribution> BuildContributions(int count)
    {
        // Mix of detector families with realistic reason text, weights, deltas.
        // ~70% have a non-empty reason; some detectors appear twice so the GroupBy
        // path has actual grouping work to do.
        var rng = new Random(42);
        string[] detectors =
        [
            "UserAgent", "Header", "Ip", "Heuristic", "Behavioral",
            "TlsFingerprint", "Http2Fingerprint", "MultiLayerCorrelation",
            "CacheBehavior", "CookieBehavior", "Inconsistency", "AiScraper",
            "ProjectHoneypot", "Reputation", "Signature"
        ];

        var result = new List<DetectionContribution>(count);
        for (var i = 0; i < count; i++)
        {
            var detector = detectors[i % detectors.Length];
            var hasReason = rng.NextDouble() < 0.7;
            result.Add(new DetectionContribution
            {
                DetectorName = detector,
                ConfidenceDelta = rng.NextDouble() * 0.4 - 0.05,
                Weight = 0.5 + rng.NextDouble(),
                Reason = hasReason ? $"{detector}: pattern matched at index {i}" : string.Empty,
                ProcessingTimeMs = rng.NextDouble() * 2.0,
                Priority = (i % 9) + 1,
                Category = "transport",
            });
        }
        return result;
    }
}