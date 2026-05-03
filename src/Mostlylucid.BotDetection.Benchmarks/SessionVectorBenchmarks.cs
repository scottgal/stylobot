using BenchmarkDotNet.Attributes;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Benchmarks for the session vector pipeline:
///     - Markov chain → vector encoding
///     - Cosine similarity comparison
///     - Velocity computation
///     - Snapshot compaction
///
///     Run: dotnet run --project Mostlylucid.BotDetection.Benchmarks -c Release -- --filter *SessionVector*
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class SessionVectorBenchmarks
{
    private IReadOnlyList<SessionRequest> _smallSession = null!;
    private IReadOnlyList<SessionRequest> _mediumSession = null!;
    private IReadOnlyList<SessionRequest> _largeSession = null!;
    private float[] _vectorA = null!;
    private float[] _vectorB = null!;
    private FingerprintContext _fingerprint = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        var states = Enum.GetValues<RequestState>();

        SessionRequest MakeRequest(int i, RequestState? forceState = null)
        {
            var state = forceState ?? states[rng.Next(states.Length)];
            return new SessionRequest(
                state,
                DateTimeOffset.UtcNow.AddSeconds(i * rng.Next(1, 30)),
                $"/path/{i % 20}",
                rng.Next(10) < 8 ? 200 : 404);
        }

        _smallSession = Enumerable.Range(0, 10).Select(i => MakeRequest(i)).ToList();
        _mediumSession = Enumerable.Range(0, 50).Select(i => MakeRequest(i)).ToList();
        _largeSession = Enumerable.Range(0, 200).Select(i => MakeRequest(i)).ToList();

        _fingerprint = new FingerprintContext
        {
            TlsVersion = 1.0f,
            HttpProtocol = 0.5f,
            ProtocolClientType = 1.0f,
            TcpOsConsistency = 1.0f,
            QuicZeroRtt = 0f,
            ClientFingerprintIntegrity = 0.8f,
            HeadlessScore = 0f,
            IsDatacenter = 0f
        };

        _vectorA = SessionVectorizer.Encode(_mediumSession, _fingerprint);
        // Slightly different session for comparison
        _vectorB = SessionVectorizer.Encode(
            Enumerable.Range(0, 50).Select(i => MakeRequest(i, states[rng.Next(states.Length)])).ToList(),
            _fingerprint);
    }

    [Benchmark(Description = "Encode 10 requests (small session)")]
    public float[] EncodeSmall() => SessionVectorizer.Encode(_smallSession);

    [Benchmark(Description = "Encode 50 requests (medium session)")]
    public float[] EncodeMedium() => SessionVectorizer.Encode(_mediumSession);

    [Benchmark(Description = "Encode 200 requests (large session)")]
    public float[] EncodeLarge() => SessionVectorizer.Encode(_largeSession);

    [Benchmark(Description = "Encode 50 requests + fingerprint")]
    public float[] EncodeMediumWithFingerprint() => SessionVectorizer.Encode(_mediumSession, _fingerprint);

    [Benchmark(Description = "Cosine similarity (118-dim)")]
    public float CosineSimilarity() => SessionVectorizer.CosineSimilarity(_vectorA, _vectorB);

    [Benchmark(Description = "Velocity computation (118-dim)")]
    public float[] ComputeVelocity() => SessionVectorizer.ComputeVelocity(_vectorA, _vectorB);

    [Benchmark(Description = "Velocity magnitude (118-dim)")]
    public float VelocityMagnitude()
    {
        var velocity = SessionVectorizer.ComputeVelocity(_vectorA, _vectorB);
        return SessionVectorizer.VelocityMagnitude(velocity);
    }

    [Benchmark(Description = "Maturity computation (50 requests)")]
    public float ComputeMaturity() => SessionVectorizer.ComputeMaturity(_mediumSession);

    [Benchmark(Description = "Full pipeline: encode + similarity + velocity")]
    public float FullPipeline()
    {
        var v1 = SessionVectorizer.Encode(_mediumSession, _fingerprint);
        var v2 = SessionVectorizer.Encode(_smallSession, _fingerprint);
        var sim = SessionVectorizer.CosineSimilarity(v1, v2);
        var vel = SessionVectorizer.ComputeVelocity(v1, v2);
        return sim + SessionVectorizer.VelocityMagnitude(vel);
    }
}
