using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Clustering;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for the new Markov chain, adaptive weighting, and geo similarity hot paths.
///     These are called per-request or per-clustering-cycle and must be fast.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class MarkovHotPathBenchmarks
{
    private MarkovTracker _tracker = null!;
    private AdaptiveSimilarityWeighter _weighter = null!;
    private List<BotClusterService.FeatureVector> _features = null!;
    private BotClusterService.FeatureVector _featureA = null!;
    private BotClusterService.FeatureVector _featureB = null!;
    private BotClusterService.FeatureVector _featureWithGeo = null!;
    private BotClusterService.FeatureVector _featureWithGeoDiff = null!;
    private Dictionary<string, double> _adaptiveWeights = null!;
    private DecayingTransitionMatrix _matrix = null!;
    private Dictionary<string, double> _distP = null!;
    private Dictionary<string, double> _distQ = null!;
    private string[] _paths = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new BotDetectionOptions
        {
            Markov = new MarkovOptions
            {
                MinTransitionsForDrift = 5,
                SignatureHalfLifeHours = 1,
                CohortHalfLifeHours = 6,
                GlobalHalfLifeHours = 24,
                MaxEdgesPerNode = 20,
                RecentTransitionWindowSize = 30
            }
        };

        _tracker = new MarkovTracker(
            NullLogger<MarkovTracker>.Instance,
            Options.Create(options));

        _weighter = new AdaptiveSimilarityWeighter(
            NullLogger<AdaptiveSimilarityWeighter>.Instance);

        // Pre-warm tracker with some transitions
        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
            _tracker.RecordTransition("warmup-sig", $"/page{i % 5}", now.AddSeconds(i), false, false, true);
        _tracker.FlushCohortUpdates();

        // Build feature vectors for similarity benchmarks
        var random = new Random(42);
        var countries = new[] { "US", "GB", "DE", "FR", "JP", "CN", "BR" };
        var asns = new[] { "AS15169", "AS13335", "AS16509", "AS8075", "AS32934" };
        _features = new List<BotClusterService.FeatureVector>();
        for (var i = 0; i < 50; i++)
        {
            _features.Add(new BotClusterService.FeatureVector
            {
                Signature = $"s{i}",
                TimingRegularity = random.NextDouble(),
                RequestRate = random.NextDouble() * 500,
                PathDiversity = random.NextDouble(),
                PathEntropy = random.NextDouble() * 5,
                AvgBotProbability = 0.5 + random.NextDouble() * 0.5,
                CountryCode = countries[random.Next(countries.Length)],
                IsDatacenter = random.Next(2) == 0,
                Asn = asns[random.Next(asns.Length)],
                FirstSeen = DateTime.UtcNow.AddMinutes(-10),
                LastSeen = DateTime.UtcNow,
                SelfDrift = random.NextDouble() * 0.5,
                HumanDrift = random.NextDouble() * 0.5,
                LoopScore = random.NextDouble() * 0.3,
                SequenceSurprise = random.NextDouble() * 5,
                TransitionNovelty = random.NextDouble() * 0.5,
                EntropyDelta = (random.NextDouble() - 0.5) * 2
            });
        }

        _featureA = _features[0];
        _featureB = _features[1];

        _featureWithGeo = _featureA with { Latitude = 51.5074, Longitude = -0.1278 };
        _featureWithGeoDiff = _featureB with { Latitude = 40.4168, Longitude = -3.7038 };

        // Pre-compute adaptive weights
        _adaptiveWeights = _weighter.ComputeWeights(_features);

        // Pre-build transition matrix for JSD benchmark
        _matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        for (var i = 0; i < 100; i++)
            _matrix.RecordTransition($"/page{i % 10}", $"/page{(i + 1) % 10}", now.AddSeconds(i));

        // Build distributions for JSD
        _distP = new Dictionary<string, double>
        {
            ["/home"] = 0.3, ["/about"] = 0.2, ["/products"] = 0.25,
            ["/cart"] = 0.15, ["/checkout"] = 0.1
        };
        _distQ = new Dictionary<string, double>
        {
            ["/home"] = 0.1, ["/about"] = 0.1, ["/products"] = 0.4,
            ["/cart"] = 0.2, ["/checkout"] = 0.2
        };

        // Common paths for normalization benchmark
        _paths = new[]
        {
            "/product/12345",
            "/api/v2/users/550e8400-e29b-41d4-a716-446655440000",
            "/assets/css/style.css",
            "/search?q=test&page=2",
            "/blog/my-awesome-long-blog-post-title-here",
            "/category/electronics/phones",
            "/",
            "/admin/settings"
        };
    }

    // ===== PER-REQUEST HOT PATH =====

    [Benchmark(Description = "RecordTransition (per-request hot path)")]
    public DriftSignals RecordTransition()
    {
        return _tracker.RecordTransition("bench-sig", "/product/123", DateTime.UtcNow, false, false, true);
    }

    [Benchmark(Description = "PathNormalizer.Normalize (per-request)")]
    public string PathNormalize()
    {
        return PathNormalizer.Normalize("/product/12345?utm_source=google&page=2");
    }

    [Benchmark(Description = "PathNormalizer.Normalize (8 diverse paths)")]
    public string PathNormalize_Diverse()
    {
        string result = null!;
        foreach (var path in _paths)
            result = PathNormalizer.Normalize(path);
        return result;
    }

    [Benchmark(Description = "PathNormalizer.Classify")]
    public string PathClassify()
    {
        return PathNormalizer.Classify("/api/v{v}/users/{id}");
    }

    // ===== PER-CLUSTERING-CYCLE =====

    [Benchmark(Description = "ComputeSimilarity (default weights)")]
    public double ComputeSimilarity_Default()
    {
        return BotClusterService.ComputeSimilarity(_featureA, _featureB);
    }

    [Benchmark(Description = "ComputeSimilarity (adaptive weights)")]
    public double ComputeSimilarity_Adaptive()
    {
        return BotClusterService.ComputeSimilarity(_featureA, _featureB, _adaptiveWeights);
    }

    [Benchmark(Description = "ComputeGeoSimilarity (Haversine)")]
    public double GeoSimilarity_Haversine()
    {
        return BotClusterService.ComputeGeoSimilarity(_featureWithGeo, _featureWithGeoDiff);
    }

    [Benchmark(Description = "ComputeGeoSimilarity (categorical)")]
    public double GeoSimilarity_Categorical()
    {
        return BotClusterService.ComputeGeoSimilarity(_featureA, _featureB);
    }

    [Benchmark(Description = "AdaptiveWeighter.ComputeWeights (50 features)")]
    public Dictionary<string, double> ComputeWeights()
    {
        return _weighter.ComputeWeights(_features);
    }

    [Benchmark(Description = "ComputeSimilarity 50x50 matrix")]
    public double SimilarityMatrix_50x50()
    {
        var total = 0.0;
        for (var i = 0; i < _features.Count; i++)
            for (var j = i + 1; j < _features.Count; j++)
                total += BotClusterService.ComputeSimilarity(_features[i], _features[j], _adaptiveWeights);
        return total;
    }

    // ===== DIVERGENCE METRICS =====

    [Benchmark(Description = "JensenShannonDivergence (5-key distributions)")]
    public double JSD()
    {
        return DivergenceMetrics.JensenShannonDivergence(_distP, _distQ);
    }

    // ===== TRANSITION MATRIX =====

    [Benchmark(Description = "TransitionMatrix.RecordTransition")]
    public void MatrixRecordTransition()
    {
        _matrix.RecordTransition("/home", "/about", DateTime.UtcNow);
    }

    [Benchmark(Description = "TransitionMatrix.GetTransitionProbability")]
    public double MatrixGetProbability()
    {
        return _matrix.GetTransitionProbability("/page0", "/page1", DateTime.UtcNow);
    }

    [Benchmark(Description = "TransitionMatrix.GetDistribution")]
    public Dictionary<string, double> MatrixGetDistribution()
    {
        return _matrix.GetDistribution("/page0", DateTime.UtcNow);
    }

    [Benchmark(Description = "TransitionMatrix.GetPathEntropy")]
    public double MatrixGetEntropy()
    {
        return _matrix.GetPathEntropy(DateTime.UtcNow);
    }

    // ===== DECAYING COUNTER =====

    [Benchmark(Description = "DecayingCounter.Decayed")]
    public double CounterDecayed()
    {
        var counter = new DecayingCounter(100.0, DateTime.UtcNow.AddMinutes(-30));
        return counter.Decayed(DateTime.UtcNow, TimeSpan.FromHours(1));
    }
}
