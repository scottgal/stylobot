using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for the BoundedVectorCache, VectorMath SIMD helpers,
///     and the Slim* similarity search hot paths.
///
///     Run from the project directory to avoid worktree csproj collisions:
///       cd src/Mostlylucid.BotDetection.Benchmarks
///       dotnet run -c Release -- --filter *SlimSimilarity*
///
///     Uses the external-process default toolchain (SimpleJob) so the suite can also be
///     run under the NativeAOT toolchain via `--runtimes nativeaot10.0`, which the
///     InProcessEmit toolchain (in-host JIT) cannot cross-compile to.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class SlimSimilaritySearchBenchmarks
{
    [Params(100, 500, 2_000, 5_000)]
    public int CacheSize { get; set; }

    private float[] _queryVector = null!;
    private float[] _refVector = null!;
    private BoundedVectorCache<string> _strCache = null!;
    private SlimSignatureSimilaritySearch _sigSearch = null!;
    private SlimSessionVectorSearch _sessSearch = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        var dims = SessionVectorizer.Dimensions;

        _queryVector = RandomVector(rng, dims);
        _refVector   = RandomVector(rng, dims);

        // Raw cache — no SQLite dependency
        _strCache = new BoundedVectorCache<string>(CacheSize);
        for (var i = 0; i < CacheSize; i++)
            _strCache.Set($"sig-{i}", $"data-{i}", isBot: i % 10 == 0);

        // SlimSignatureSimilaritySearch with NullCentroidStore (no SQLite I/O)
        var sigOpts = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions { SignatureCacheSize = CacheSize }
        });
        _sigSearch = new SlimSignatureSimilaritySearch(
            sigOpts, new NullSignatureCentroidStore(),
            NullLogger<SlimSignatureSimilaritySearch>.Instance);
        for (var i = 0; i < CacheSize; i++)
            _sigSearch.AddAsync(RandomVector(rng, dims), $"sig-{i}",
                wasBot: i % 5 == 0, confidence: rng.NextDouble()).GetAwaiter().GetResult();

        // SlimSessionVectorSearch with NullCentroidStore
        var sessOpts = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions { SessionCacheSize = CacheSize }
        });
        _sessSearch = new SlimSessionVectorSearch(
            sessOpts, new NullSessionCentroidStore(),
            NullLogger<SlimSessionVectorSearch>.Instance);
        for (var i = 0; i < CacheSize; i++)
            _sessSearch.AddAsync(RandomVector(rng, dims), $"sess-{i}",
                isBot: i % 5 == 0, botProbability: rng.NextDouble()).GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------
    // VectorMath micro-benchmarks (independent of CacheSize param)
    // -----------------------------------------------------------------------

    [Benchmark(Description = "VectorMath.CosineSimilarity SIMD (129-dim)")]
    public float CosineSimilarity() => VectorMath.CosineSimilarity(_queryVector, _refVector);

    [Benchmark(Description = "VectorMath.IsValidVector (129-dim)")]
    public bool IsValidVector() => VectorMath.IsValidVector(_queryVector);

    // -----------------------------------------------------------------------
    // BoundedVectorCache ops
    // -----------------------------------------------------------------------

    [Benchmark(Description = "BoundedVectorCache.TryGet (hit)")]
    public bool CacheTryGetHit() => _strCache.TryGet("sig-0", out _);

    [Benchmark(Description = "BoundedVectorCache.TryGet (miss)")]
    public bool CacheTryGetMiss() => _strCache.TryGet("__miss__", out _);

    [Benchmark(Description = "BoundedVectorCache.Touch")]
    public void CacheTouch() => _strCache.Touch("sig-0");

    [Benchmark(Description = "BoundedVectorCache.GetAll full scan")]
    public int CacheGetAllScan()
    {
        var n = 0;
        foreach (var _ in _strCache.GetAll()) n++;
        return n;
    }

    // -----------------------------------------------------------------------
    // Similarity search hot paths — scales with CacheSize
    // -----------------------------------------------------------------------

    [Benchmark(Description = "SlimSignature.FindSimilarAsync (top5, thresh=0.80)")]
    public Task<IReadOnlyList<SimilarSignature>> FindSimilarSignatures()
        => _sigSearch.FindSimilarAsync(_queryVector, topK: 5, minSimilarity: 0.80f);

    [Benchmark(Description = "SlimSession.FindSimilarAsync (top10, thresh=0.70)")]
    public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarSessions()
        => _sessSearch.FindSimilarAsync(_queryVector, topK: 10, minSimilarity: 0.70f);

    [Benchmark(Description = "SlimSession.FindSimilarMahalanobisAsync (top10, maxDist=5)")]
    public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobis()
        => _sessSearch.FindSimilarMahalanobisAsync(_queryVector, topK: 10, maxDistance: 5.0f);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static float[] RandomVector(Random rng, int dims)
    {
        var v = new float[dims];
        for (var i = 0; i < dims; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }
}
