using System.Collections.Immutable;
using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Hot-path allocation + throughput for everything new in the action
///     surface this session (Phase 1 throttle-tools fix + bucket TTL +
///     silent-drop + stable challenge binding, Phase 2 subnet/asn keys +
///     sticky-deny + per-endpoint resolver). The action policy + key
///     derivation runs on EVERY detected-bot request -- every nanosecond
///     and every byte allocated multiplies by the request rate.
///
///     <para>
///     Baselines worth comparing against (from
///     <see cref="VerdictCacheBenchmarks"/> + the YAML detector scenarios):
///     individual detectors run 130 ns - 4 us with allocations under 2 KB.
///     Anything in the action surface drifting past that needs scrutiny.
///     </para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class ActionSurfacePhase12Benchmarks
{
    /// <summary>
    ///     InProcess toolchain skips the auto-generated boilerplate-csproj
    ///     step, which can't tell which of the four duplicate-named
    ///     benchmark csprojs (root + 3 worktrees) to pick. Accuracy is
    ///     fine for the action-surface micro-benchmarks; the production
    ///     CI runs the full external-process job from a clean checkout.
    /// </summary>
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithStrategy(RunStrategy.Throughput)
                .WithWarmupCount(3)
                .WithIterationCount(10));
        }
    }

    // Shared resources
    private SilentDropActionPolicy _silentDrop = null!;
    private ThrottleActionPolicy _throttleTools = null!;
    private ThrottleActionPolicy _throttleStealth = null!;
    private InMemoryTokenBucketStore _bucketStore = null!;
    private InMemoryTokenBucketStore _bucketStoreSmallCap = null!;
    private RateLimitActionPolicy _rlSignature = null!;
    private RateLimitActionPolicy _rlIp = null!;
    private RateLimitActionPolicy _rlSubnet = null!;
    private RateLimitActionPolicy _rlAsn = null!;
    private RateLimitActionPolicy _rlAsnSig = null!;
    private InMemoryStickyDenyTracker _stickyTrackerEmpty = null!;
    private InMemoryStickyDenyTracker _stickyTrackerWithKey = null!;
    private StickyDenyActionPolicy _stickyDenyUnderThreshold = null!;
    private StickyDenyActionPolicy _stickyDenyBlocked = null!;
    private ChallengeActionPolicy _challengeStable = null!;
    private ChallengeActionPolicy _challengeStrict = null!;
    private string _validStableToken = null!;
    private string _validStrictToken = null!;
    private Endpoint _endpointWithBotAction = null!;
    private Endpoint _endpointEmpty = null!;
    private ActionPolicyRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registryEvidence = NewEvidence(0.95, "sig:bench");
        var underInner = new ImmediateActionPolicy("under-inner");
        _registry = new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            new IActionPolicy[] { underInner });

        _silentDrop = new SilentDropActionPolicy("silent-drop");
        _throttleTools = new ThrottleActionPolicy("throttle-tools", ThrottleActionOptions.Tools);

        // Stealth with a tiny base delay so the benchmark doesn't sleep
        // for seconds; the codepath shape is what matters.
        _throttleStealth = new ThrottleActionPolicy("throttle-stealth-bench",
            new ThrottleActionOptions
            {
                BaseDelayMs = 1, MinDelayMs = 0, MaxDelayMs = 1, JitterPercent = 0,
                ScaleByRisk = false, ReturnStatus = false,
            });

        _bucketStore = new InMemoryTokenBucketStore();
        // Tiny cap forces the LFU sweep to fire frequently; baseline for the
        // worst-case sweep cost.
        _bucketStoreSmallCap = new InMemoryTokenBucketStore(
            new InMemoryTokenBucketStoreOptions { MaxBuckets = 100, SweepEveryN = 16, IdleTtl = TimeSpan.FromSeconds(5) });

        static RateLimitActionOptions RlOpts(RateLimitKey k) => new()
        {
            RequestsPerMinute = 600_000, BurstSize = 1_000_000, KeyBy = k,
        };
        _rlSignature = new RateLimitActionPolicy("rl-sig", RlOpts(RateLimitKey.Signature), _bucketStore, _registry);
        _rlIp = new RateLimitActionPolicy("rl-ip", RlOpts(RateLimitKey.Ip), _bucketStore, _registry);
        _rlSubnet = new RateLimitActionPolicy("rl-subnet", RlOpts(RateLimitKey.Subnet), _bucketStore, _registry);
        _rlAsn = new RateLimitActionPolicy("rl-asn", RlOpts(RateLimitKey.Asn), _bucketStore, _registry);
        _rlAsnSig = new RateLimitActionPolicy("rl-asn-sig", RlOpts(RateLimitKey.AsnAndSignature), _bucketStore, _registry);

        var stickyOpts = new StickyDenyActionOptions
        {
            ViolationThreshold = 100_000_000, // never trip in benchmark window
            UnderThresholdAction = underInner.Name,
            SetCookie = false,
        };
        var stickyOptsBlocked = new StickyDenyActionOptions
        {
            ViolationThreshold = 1, // trip on first violation
            UnderThresholdAction = underInner.Name,
            SetCookie = false,
        };
        _stickyTrackerEmpty = new InMemoryStickyDenyTracker(stickyOpts);
        _stickyTrackerWithKey = new InMemoryStickyDenyTracker(stickyOpts);
        // Pre-populate one key so IsBlocked has a hot-tier hit.
        for (var i = 0; i < 1000; i++)
            _stickyTrackerWithKey.RecordViolation("203.0.113.5", DateTimeOffset.UnixEpoch.AddMilliseconds(i));
        _stickyDenyUnderThreshold = new StickyDenyActionPolicy("sticky-under",
            stickyOpts, _stickyTrackerEmpty, _registry);

        var blockedTracker = new InMemoryStickyDenyTracker(stickyOptsBlocked);
        blockedTracker.RecordViolation("203.0.113.5", DateTimeOffset.UnixEpoch); // pre-block the key
        _stickyDenyBlocked = new StickyDenyActionPolicy("sticky-blocked",
            stickyOptsBlocked, blockedTracker, _registry);

        var chOptsStable = new ChallengeActionOptions
        {
            UseTokens = true,
            TokenBindingMode = ChallengeTokenBindingMode.Stable,
            TokenSecret = "bench-secret-32-chars-or-more-long-ok",
        };
        var chOptsStrict = new ChallengeActionOptions
        {
            UseTokens = true,
            TokenBindingMode = ChallengeTokenBindingMode.Strict,
            TokenSecret = "bench-secret-32-chars-or-more-long-ok",
        };
        _challengeStable = new ChallengeActionPolicy("challenge-stable", chOptsStable);
        _challengeStrict = new ChallengeActionPolicy("challenge-strict", chOptsStrict);
        var tokenCtx = NewBenchContextWithEvidence("sig:bench");
        _validStableToken = ChallengeActionPolicy.GenerateChallengeToken(tokenCtx, chOptsStable);
        _validStrictToken = ChallengeActionPolicy.GenerateChallengeToken(tokenCtx, chOptsStrict);

        _endpointWithBotAction = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new BotActionAttribute("recording-stub")),
            "bench-with-attr");
        _endpointEmpty = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "bench-empty");
    }

    // === EndpointActionPolicyResolver (runs in middleware per request) ====

    [Benchmark(Description = "EndpointResolver.NoAttributes")]
    public string? Resolver_NoAttributes()
    {
        var ctx = new DefaultHttpContext();
        ctx.SetEndpoint(_endpointEmpty);
        return EndpointActionPolicyResolver.ResolveFromEndpoint(ctx);
    }

    [Benchmark(Description = "EndpointResolver.WithBotAction")]
    public string? Resolver_WithBotAction()
    {
        var ctx = new DefaultHttpContext();
        ctx.SetEndpoint(_endpointWithBotAction);
        return EndpointActionPolicyResolver.ResolveFromEndpoint(ctx);
    }

    [Benchmark(Description = "EndpointResolver.NullEndpoint")]
    public string? Resolver_NullEndpoint()
    {
        return EndpointActionPolicyResolver.ResolveFromEndpoint(new DefaultHttpContext());
    }

    // === RateLimit key derivation (one of these runs on every gated request) ===

    [Benchmark(Description = "RateLimit.Signature (baseline)", Baseline = true)]
    public async Task<bool> RateLimit_Signature()
    {
        var ctx = NewBenchContext();
        var r = await _rlSignature.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.Continue;
    }

    [Benchmark(Description = "RateLimit.Ip")]
    public async Task<bool> RateLimit_Ip()
    {
        var ctx = NewBenchContext();
        var r = await _rlIp.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.Continue;
    }

    [Benchmark(Description = "RateLimit.Subnet (new IPv4 /24)")]
    public async Task<bool> RateLimit_Subnet()
    {
        var ctx = NewBenchContext();
        var r = await _rlSubnet.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.Continue;
    }

    [Benchmark(Description = "RateLimit.Asn (new)")]
    public async Task<bool> RateLimit_Asn()
    {
        var ctx = NewBenchContext();
        var r = await _rlAsn.ExecuteAsync(ctx, NewEvidenceWithAsn(0.95, "sig:bench", 12345));
        return r.Continue;
    }

    [Benchmark(Description = "RateLimit.AsnAndSignature (new composite)")]
    public async Task<bool> RateLimit_AsnAndSignature()
    {
        var ctx = NewBenchContext();
        var r = await _rlAsnSig.ExecuteAsync(ctx, NewEvidenceWithAsn(0.95, "sig:bench", 12345));
        return r.Continue;
    }

    // === InMemoryTokenBucketStore: with-cap (TTL + LFU sweep) cost ========

    [Benchmark(Description = "TokenBucket.TryConsume (no cap)")]
    public bool Bucket_NoCap()
    {
        return _bucketStore.TryConsume("p", "sig:bench", capacity: 10, refillRatePerMinute: 600);
    }

    [Benchmark(Description = "TokenBucket.TryConsume (cap=100, sweep every 16)")]
    public bool Bucket_WithCap()
    {
        return _bucketStoreSmallCap.TryConsume("p", "sig:bench", capacity: 10, refillRatePerMinute: 600);
    }

    // === SilentDropActionPolicy: trivially fast new policy ================

    [Benchmark(Description = "SilentDrop.ExecuteAsync (calls Abort)")]
    public async Task<int> SilentDrop_Execute()
    {
        var ctx = NewBenchContext();
        var r = await _silentDrop.ExecuteAsync(ctx, NewEvidence(0.99, "sig:bench"));
        return r.StatusCode;
    }

    // === Throttle fix path: 429 + Retry-After WITHOUT wall-clock delay ====

    [Benchmark(Description = "ThrottleTools.ExecuteAsync (FIX: no wall-clock hold)")]
    public async Task<int> ThrottleTools_Execute()
    {
        var ctx = NewBenchContext();
        var r = await _throttleTools.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.StatusCode;
    }

    // === StickyDeny tracker: in-memory CAS perf =========================

    [Benchmark(Description = "StickyDeny.IsBlocked (hot-tier hit)")]
    public bool StickyDeny_IsBlocked()
    {
        return _stickyTrackerWithKey.IsBlocked("203.0.113.5", DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    [Benchmark(Description = "StickyDeny.RecordViolation (CAS loop)")]
    public bool StickyDeny_Record()
    {
        return _stickyTrackerEmpty.RecordViolation("203.0.113.5", DateTimeOffset.UnixEpoch);
    }

    [Benchmark(Description = "StickyDenyPolicy.Execute (under threshold, delegates)")]
    public async Task<int> StickyDeny_UnderThreshold()
    {
        var ctx = NewBenchContext();
        var r = await _stickyDenyUnderThreshold.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.StatusCode;
    }

    [Benchmark(Description = "StickyDenyPolicy.Execute (blocked, short-circuits)")]
    public async Task<int> StickyDeny_Blocked()
    {
        var ctx = NewBenchContext();
        var r = await _stickyDenyBlocked.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.StatusCode;
    }

    // === Challenge token verify: stable vs strict =======================

    [Benchmark(Description = "Challenge.HasValidToken (Stable mode, valid token)")]
    public async Task<bool> Challenge_Stable_ValidToken()
    {
        var ctx = NewBenchContextWithEvidence("sig:bench");
        ctx.Request.Headers.Cookie = $"bot_challenge_token={_validStableToken}";
        var r = await _challengeStable.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.Continue;
    }

    [Benchmark(Description = "Challenge.HasValidToken (Strict mode, valid token)")]
    public async Task<bool> Challenge_Strict_ValidToken()
    {
        var ctx = NewBenchContextWithEvidence("sig:bench");
        ctx.Request.Headers.Cookie = $"bot_challenge_token={_validStrictToken}";
        var r = await _challengeStrict.ExecuteAsync(ctx, NewEvidence(0.95, "sig:bench"));
        return r.Continue;
    }

    // === Helpers ============================================================

    private static HttpContext NewBenchContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        ctx.Request.Host = new HostString("bench.example.com");
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 bench";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        return ctx;
    }

    private static HttpContext NewBenchContextWithEvidence(string sig)
    {
        var ctx = NewBenchContext();
        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = NewEvidence(0.95, sig);
        return ctx;
    }

    private static AggregatedEvidence NewEvidence(double prob, string sig) => new()
    {
        Ledger = new DetectionLedger("bench"),
        BotProbability = prob,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        CategoryBreakdown = new Dictionary<string, CategoryScore>(),
        Signals = new Dictionary<string, object> { [SignalKeys.PrimarySignature] = sig }.ToImmutableDictionary(),
        ContributingDetectors = new HashSet<string>(),
        FailedDetectors = new HashSet<string>(),
    };

    private static AggregatedEvidence NewEvidenceWithAsn(double prob, string sig, int asn) => new()
    {
        Ledger = new DetectionLedger("bench"),
        BotProbability = prob,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        CategoryBreakdown = new Dictionary<string, CategoryScore>(),
        Signals = new Dictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = sig,
            [SignalKeys.IpAsn] = asn,
        }.ToImmutableDictionary(),
        ContributingDetectors = new HashSet<string>(),
        FailedDetectors = new HashSet<string>(),
    };

    /// <summary>Inner-action stub that returns immediately (no allocation, no async).</summary>
    private sealed class ImmediateActionPolicy : IActionPolicy
    {
        public ImmediateActionPolicy(string name) => Name = name;
        public string Name { get; }
        public ActionType ActionType => ActionType.Throttle;
        public PolicyIntent Intent => PolicyIntent.Throttle;
        public Task<ActionResult> ExecuteAsync(HttpContext c, AggregatedEvidence e, CancellationToken ct = default)
            => Task.FromResult(ActionResult.Blocked(429, "bench stub"));
    }
}
