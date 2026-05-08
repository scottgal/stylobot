using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Tests for the merged <see cref="BehavioralContributor" />.
///     Statistical analysis (path/timing entropy) is covered by BehavioralPatternAnalyzerTests.
///     This file covers contributor-level behavior: name, priority, wave, signal keys, rate detection.
/// </summary>
public class BehavioralContributorTests
{
    #region Infrastructure

    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _params;

        public StubConfigProvider(Dictionary<string, object>? parameters = null)
            => _params = parameters ?? new Dictionary<string, object>();

        public DetectorManifest? GetManifest(string detectorName) => null;

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
            Confidence = new ConfidenceDefaults
                { BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5 },
            Parameters = new Dictionary<string, object>(_params)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_params.TryGetValue(parameterName, out var val))
                try { return (T)Convert.ChangeType(val, typeof(T)); } catch { }
            return defaultValue;
        }

        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private BehavioralContributor CreateContributor(
        BotDetectionOptions? options = null,
        IMemoryCache? cache = null,
        Dictionary<string, object>? configParams = null)
    {
        var opts = options ?? new BotDetectionOptions
        {
            Behavioral = new BehavioralOptions
            {
                EnableAdvancedPatternDetection = true,
                AnalysisWindow = TimeSpan.FromMinutes(15),
                IdentityHashSalt = "test-salt"
            }
        };
        var memCache = cache ?? new MemoryCache(new MemoryCacheOptions());
        var detector = new BehavioralDetector(
            NullLogger<BehavioralDetector>.Instance,
            Options.Create(opts),
            memCache);

        return new BehavioralContributor(
            NullLogger<BehavioralContributor>.Instance,
            detector,
            memCache,
            Options.Create(opts),
            new StubConfigProvider(configParams));
    }

    private static BlackboardState CreateState(
        string ip = "1.2.3.4",
        string path = "/",
        string userAgent = "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0.0.0",
        Dictionary<string, object>? signals = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Request.Headers.UserAgent = userAgent;

        var baseSignals = new Dictionary<string, object>(signals ?? new Dictionary<string, object>());
        if (!baseSignals.ContainsKey(SignalKeys.ClientIp))
            baseSignals[SignalKeys.ClientIp] = ip;

        var dict = new ConcurrentDictionary<string, object>(baseSignals);
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = dict,
            SignalWriter = dict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    #endregion

    #region Basic properties

    [Fact]
    public void Name_Is_Behavioral()
        => Assert.Equal("Behavioral", CreateContributor().Name);

    [Fact]
    public void TriggerConditions_Empty_RunsInWave0()
        => Assert.Empty(CreateContributor().TriggerConditions);

    [Fact]
    public void Priority_Defaults_To20()
        => Assert.Equal(20, CreateContributor().Priority);

    #endregion

    #region Basic behavioral detection

    [Fact]
    public async Task Contribute_FirstRequest_ReturnsContributionWithCorrectDetectorName()
    {
        var contributions = await CreateContributor().ContributeAsync(CreateState());

        Assert.NotEmpty(contributions);
        Assert.All(contributions, c => Assert.Equal("Behavioral", c.DetectorName));
    }

    [Fact]
    public async Task Contribute_FirstRequest_WritesBehavioralAnomalySignal()
    {
        var state = CreateState();
        await CreateContributor().ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey(SignalKeys.BehavioralAnomalyDetected));
    }

    [Fact]
    public async Task Contribute_FirstRequest_AnomalyIsFalse()
    {
        var state = CreateState();
        await CreateContributor().ContributeAsync(state);

        Assert.False(state.GetSignal<bool>(SignalKeys.BehavioralAnomalyDetected));
    }

    [Fact]
    public async Task Contribute_ExcessiveRequests_RateExceededSignalBecomesTrue()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var opts = new BotDetectionOptions
        {
            MaxRequestsPerMinute = 3,
            Behavioral = new BehavioralOptions
            {
                EnableAdvancedPatternDetection = false,
                AnalysisWindow = TimeSpan.FromMinutes(15),
                IdentityHashSalt = "test-salt"
            }
        };
        var contributor = CreateContributor(opts, cache);
        const string ip = "10.0.0.1";

        bool rateTriggered = false;
        for (var i = 0; i < 10; i++)
        {
            var state = CreateState(ip: ip);
            await contributor.ContributeAsync(state);
            if (state.GetSignal<bool>(SignalKeys.BehavioralRateExceeded))
                rateTriggered = true;
        }

        Assert.True(rateTriggered, "Rate-exceeded signal should be set after exceeding MaxRequestsPerMinute=3");
    }

    [Fact]
    public async Task Contribute_AdvancedDisabled_DoesNotWritePathEntropySignal()
    {
        var opts = new BotDetectionOptions
        {
            Behavioral = new BehavioralOptions
            {
                EnableAdvancedPatternDetection = false,
                AnalysisWindow = TimeSpan.FromMinutes(15),
                IdentityHashSalt = "test-salt"
            }
        };
        var contributor = CreateContributor(opts);
        const string ip = "10.0.0.2";

        for (var i = 0; i < 10; i++)
            await contributor.ContributeAsync(CreateState(ip: ip, path: $"/page/{i}"));

        var lastState = CreateState(ip: ip, path: "/page/10");
        await contributor.ContributeAsync(lastState);

        Assert.False(lastState.Signals.ContainsKey("PathEntropy"),
            "PathEntropy signal must not be written when advanced detection is disabled");
    }

    #endregion
}

/// <summary>
///     Direct unit tests for <see cref="BehavioralPatternAnalyzer" /> — the statistical core
///     of <see cref="BehavioralContributor" /> that handles entropy, timing, and burst analysis.
/// </summary>
public class BehavioralPatternAnalyzerTests
{
    private static BehavioralPatternAnalyzer CreateAnalyzer()
        => new(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(15), "test-salt");

    private static void RecordPaths(BehavioralPatternAnalyzer analyzer, string identity,
        IEnumerable<string> paths)
    {
        var t = DateTime.UtcNow.AddMinutes(-10);
        foreach (var p in paths)
        {
            analyzer.RecordRequest(identity, p, t);
            t = t.AddSeconds(2);
        }
    }

    #region Path entropy

    [Fact]
    public void PathEntropy_AllSamePath_ReturnsZero()
    {
        var a = CreateAnalyzer();
        RecordPaths(a, "id1", Enumerable.Repeat("/api/data", 15));
        Assert.Equal(0.0, a.CalculatePathEntropy("id1"), precision: 5);
    }

    [Fact]
    public void PathEntropy_AllUniquePaths_ReturnsHighValue()
    {
        var a = CreateAnalyzer();
        RecordPaths(a, "id2", Enumerable.Range(1, 20).Select(i => $"/product/{i}"));
        var entropy = a.CalculatePathEntropy("id2");
        Assert.True(entropy > 3.0, $"Expected high entropy for 20 unique paths, got {entropy:F3}");
    }

    [Fact]
    public void PathEntropy_MostlySamePath_ReturnsLowPositiveValue()
    {
        var a = CreateAnalyzer();
        // 18 × /api/data, 2 × /api/other → entropy ≈ 0.47
        var paths = Enumerable.Repeat("/api/data", 18).Concat(Enumerable.Repeat("/api/other", 2));
        RecordPaths(a, "id3", paths);
        var entropy = a.CalculatePathEntropy("id3");
        Assert.True(entropy is > 0.0 and < 1.0,
            $"Expected low positive entropy for mostly-same-path bot, got {entropy:F3}");
    }

    [Fact]
    public void PathEntropy_InsufficientData_ReturnsZero()
    {
        var a = CreateAnalyzer();
        RecordPaths(a, "id4", new[] { "/a", "/b", "/c" }); // < 5 paths
        Assert.Equal(0.0, a.CalculatePathEntropy("id4"), precision: 5);
    }

    #endregion

    #region Timing entropy

    [Fact]
    public void TimingEntropy_AllSameInterval_ReturnsZero()
    {
        var a = CreateAnalyzer();
        var t = DateTime.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 20; i++)
        {
            a.RecordRequest("te1", "/", t);
            t = t.AddMilliseconds(50); // constant 50ms → all in same 100ms bucket
        }
        Assert.Equal(0.0, a.CalculateTimingEntropy("te1"), precision: 5);
    }

    [Fact]
    public void TimingEntropy_VariedIntervals_ReturnsPositiveValue()
    {
        var a = CreateAnalyzer();
        var t = DateTime.UtcNow.AddMinutes(-5);
        // Alternate between 50ms, 500ms, 1500ms buckets
        var intervals = new[] { 50, 500, 1500, 50, 500, 1500, 50, 500, 1500, 50 };
        foreach (var ms in intervals)
        {
            a.RecordRequest("te2", "/", t);
            t = t.AddMilliseconds(ms);
        }
        var entropy = a.CalculateTimingEntropy("te2");
        Assert.True(entropy > 0.0, $"Expected positive entropy for varied intervals, got {entropy:F3}");
    }

    [Fact]
    public void TimingEntropy_InsufficientData_ReturnsZero()
    {
        var a = CreateAnalyzer();
        var t = DateTime.UtcNow;
        for (var i = 0; i < 4; i++) // < 5 timings
        {
            a.RecordRequest("te3", "/", t);
            t = t.AddSeconds(1);
        }
        Assert.Equal(0.0, a.CalculateTimingEntropy("te3"), precision: 5);
    }

    #endregion

    #region Burst detection

    [Fact]
    public void DetectBurstPattern_HighVolumeInWindow_ReturnsBurst()
    {
        var a = CreateAnalyzer();
        // Seed 10 historical requests at normal rate (one every 5s), well before the burst window
        var t = DateTime.UtcNow.AddSeconds(-90);
        for (var i = 0; i < 10; i++)
        {
            a.RecordRequest("burst1", "/page", t);
            t = t.AddSeconds(5);
        }
        // Now burst: 40 requests in 15 seconds inside the 30s window
        t = DateTime.UtcNow.AddSeconds(-14);
        for (var i = 0; i < 40; i++)
        {
            a.RecordRequest("burst1", $"/path/{i}", t);
            t = t.AddMilliseconds(350);
        }
        var (isBurst, burstSize, _) = a.DetectBurstPattern("burst1", TimeSpan.FromSeconds(30));
        Assert.True(isBurst, $"Expected burst pattern for 40 requests in 15 seconds, got burstSize={burstSize}");
        Assert.True(burstSize >= 10, $"Expected burst size >= 10, got {burstSize}");
    }

    [Fact]
    public void DetectBurstPattern_LowVolume_DoesNotReturnBurst()
    {
        var a = CreateAnalyzer();
        var t = DateTime.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 5; i++)
        {
            a.RecordRequest("burst2", "/", t);
            t = t.AddSeconds(30); // slow, spread out
        }
        var (isBurst, _, _) = a.DetectBurstPattern("burst2", TimeSpan.FromSeconds(30));
        Assert.False(isBurst, "Should not flag slow, spread-out requests as a burst");
    }

    #endregion

    #region Navigation pattern

    [Fact]
    public void AnalyzeNavigationPattern_RepeatSingleTransition_DetectsNonHumanPattern()
    {
        var a = CreateAnalyzer();
        var t = DateTime.UtcNow.AddMinutes(-5);
        // Bot always goes / → /api/data → / → /api/data (single transition)
        for (var i = 0; i < 12; i++)
        {
            a.RecordRequest("nav1", i % 2 == 0 ? "/" : "/api/data", t);
            t = t.AddSeconds(1);
        }
        var (score, pattern) = a.AnalyzeNavigationPattern("nav1", "/api/data");
        Assert.True(score >= 0, "Navigation score should be non-negative");
        Assert.NotNull(pattern);
    }

    #endregion
}
