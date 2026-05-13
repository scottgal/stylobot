using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Hot-path allocation and throughput for the fingerprint verdict cache.
///     The Skip path runs on every cached request: every nanosecond and every byte
///     allocated here gets multiplied by the request rate.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class VerdictCacheBenchmarks
{
    private const string KnownSignature = "bench-known";
    private const string UnknownSignature = "bench-unknown";

    private SignatureCoordinator _coordinator = null!;
    private SignatureVerdictGate _gate = null!;
    private VarianceWatchdog _watchdog = null!;
    private FingerprintPriorContributor _priorContributor = null!;

    private SignatureCacheOptions _cacheOptionsSkip = null!;
    private SignatureCacheOptions _cacheOptionsBias = null!;
    private VarianceWatchdogOptions _watchdogOptions = null!;
    private SignatureVerdict _cachedHuman = null!;
    private DefaultHttpContext _ctxSameIp = null!;
    private DefaultHttpContext _ctxDifferentIp = null!;
    private BlackboardState _stateWithPrior = null!;
    private BlackboardState _stateNoPrior = null!;

    [GlobalSetup]
    public void Setup()
    {
        var coordOpts = Options.Create(new BotDetectionOptions
        {
            SignatureCoordinator = new SignatureCoordinatorOptions
            {
                MaxSignaturesInWindow = 1000,
                SignatureWindow = TimeSpan.FromMinutes(15),
                MaxRequestsPerSignature = 100,
            },
        });
        _coordinator = new SignatureCoordinator(NullLogger<SignatureCoordinator>.Instance, coordOpts);

        // Prime the coordinator's sliding window so KnownSignature has a confident
        // verdict ready for Skip-path benchmarks.
        for (var i = 0; i < 20; i++)
        {
            _coordinator.RecordRequestAsync(
                signature: KnownSignature,
                requestId: $"warmup-{i}",
                path: "/api",
                botProbability: 0.05,
                signals: new Dictionary<string, object>(),
                detectorsRan: new HashSet<string> { "Stub" }).GetAwaiter().GetResult();
        }
        // Wait for the keyed-sequential atom to drain.
        for (var i = 0; i < 100 && _coordinator.TryGetVerdictAsync(KnownSignature).GetAwaiter().GetResult() is null; i++)
            Thread.Sleep(10);

        _gate = new SignatureVerdictGate(_coordinator, NullLogger<SignatureVerdictGate>.Instance);
        _watchdog = new VarianceWatchdog(NullLogger<VarianceWatchdog>.Instance);
        _watchdog.RecordObservation(KnownSignature, "10.0.0.5", "/api");

        _priorContributor = new FingerprintPriorContributor(
            NullLogger<FingerprintPriorContributor>.Instance,
            new StaticConfigProvider());

        _cacheOptionsSkip = new SignatureCacheOptions
        {
            SkipMinConfidence = 0.5,
            SkipMaxAgeSeconds = 3600,
            BiasMinConfidence = 0.3,
            BiasMaxAgeSeconds = 86_400,
            SkipSamplingRate = 0.0,
        };
        _cacheOptionsBias = _cacheOptionsSkip with { SkipMinConfidence = 0.99 };
        _watchdogOptions = new VarianceWatchdogOptions();

        _cachedHuman = _coordinator.TryGetVerdictAsync(KnownSignature).GetAwaiter().GetResult()!;
        _ctxSameIp = MakeCtx("10.0.0.5", "/api");
        _ctxDifferentIp = MakeCtx("10.1.0.5", "/api");

        var withPriorSignals = new System.Collections.Concurrent.ConcurrentDictionary<string, object>();
        withPriorSignals[SignalKeys.FingerprintPriorProbability] = 0.05;
        withPriorSignals[SignalKeys.FingerprintPriorConfidence] = 0.8;
        withPriorSignals[SignalKeys.FingerprintPriorAgeSeconds] = 30.0;
        _stateWithPrior = MakeState(withPriorSignals);

        _stateNoPrior = MakeState(new System.Collections.Concurrent.ConcurrentDictionary<string, object>());
    }

    private static DefaultHttpContext MakeCtx(string ip, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        ctx.Request.Path = path;
        return ctx;
    }

    private static BlackboardState MakeState(System.Collections.Concurrent.ConcurrentDictionary<string, object> signals)
        => new()
        {
            HttpContext = new DefaultHttpContext(),
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = System.Collections.Immutable.ImmutableHashSet<string>.Empty,
            FailedDetectors = System.Collections.Immutable.ImmutableHashSet<string>.Empty,
            Contributions = System.Collections.Immutable.ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero,
        };

    // ── SignatureVerdictGate ──────────────────────────────────────────────────

    [Benchmark]
    public async Task<GateDecision> Gate_Skip_HotPath()
        => await _gate.DecideAsync(KnownSignature, _cacheOptionsSkip);

    [Benchmark]
    public async Task<GateDecision> Gate_Bias()
        => await _gate.DecideAsync(KnownSignature, _cacheOptionsBias);

    [Benchmark]
    public async Task<GateDecision> Gate_Miss_NoSignature()
        => await _gate.DecideAsync(null, _cacheOptionsSkip);

    [Benchmark]
    public async Task<GateDecision> Gate_Miss_UnknownSignature()
        => await _gate.DecideAsync(UnknownSignature, _cacheOptionsSkip);

    // ── VarianceWatchdog ──────────────────────────────────────────────────────

    [Benchmark]
    public WatchdogResult Watchdog_Check_NotTripped()
        => _watchdog.Check(_ctxSameIp, KnownSignature, _cachedHuman, _watchdogOptions);

    [Benchmark]
    public WatchdogResult Watchdog_Check_IpRotation_Trips()
        => _watchdog.Check(_ctxDifferentIp, KnownSignature, _cachedHuman, _watchdogOptions);

    [Benchmark]
    public void Watchdog_RecordObservation()
        => _watchdog.RecordObservation(KnownSignature, "10.0.0.5", "/api");

    // ── SignatureCoordinator observation paths ────────────────────────────────

    [Benchmark]
    public Task Coordinator_NotifyObservation_Lightweight()
        => _coordinator.NotifyObservationAsync(KnownSignature, "/api", 0.05);

    // ── FingerprintPriorContributor ───────────────────────────────────────────

    [Benchmark]
    public Task<IReadOnlyList<DetectionContribution>> PriorContributor_Miss_NoPrior()
        => _priorContributor.ContributeAsync(_stateNoPrior);

    [Benchmark]
    public Task<IReadOnlyList<DetectionContribution>> PriorContributor_Bias_WithPrior()
        => _priorContributor.ContributeAsync(_stateWithPrior);

    // Minimal stub config provider so we don't allocate manifests / option sources.
    private sealed class StaticConfigProvider : IDetectorConfigProvider
    {
        private static readonly DetectorDefaults _defaults = new()
        {
            Weights = new WeightDefaults { Base = 0.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 0.0 },
            Confidence = new ConfidenceDefaults { BotDetected = 0.5, HumanIndicated = -0.5, Neutral = 0.0, StrongSignal = 0.8 },
            Parameters = new Dictionary<string, object>(),
        };
        public DetectorManifest? GetManifest(string detectorName) => null;
        public DetectorDefaults GetDefaults(string detectorName) => _defaults;
        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;
        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName, ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(defaultValue);
        public void InvalidateCache(string? detectorName = null) { }
        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests() => new Dictionary<string, DetectorManifest>();
    }
}
