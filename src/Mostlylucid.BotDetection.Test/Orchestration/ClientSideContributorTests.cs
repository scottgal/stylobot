using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
/// Tests for <see cref="FingerprintPopulationTracker"/> and <see cref="ClientSideContributor"/>
/// adaptive "no fingerprint" logic.
/// </summary>
public class ClientSideContributorTests
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
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.8, HumanSignal = 1.5, Verified = 2.0 },
            Confidence = new ConfidenceDefaults { BotDetected = 0.5, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.8 },
            Parameters = new Dictionary<string, object>(_params)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_params.TryGetValue(parameterName, out var val))
            {
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private static BlackboardState CreateState(
        string transportClass = "document",
        string uaFamily = "Chrome",
        Action<HttpContext>? configureHttp = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 Chrome/120.0.0.0";
        configureHttp?.Invoke(ctx);

        var signals = new ConcurrentDictionary<string, object>();
        signals[SignalKeys.TransportProtocolClass] = transportClass;
        signals[SignalKeys.UserAgentFamily] = uaFamily;
        signals[SignalKeys.PrimarySignature] = "test-sig";

        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    private static ClientSideContributor CreateContributor(
        IBrowserFingerprintStore? store = null,
        FingerprintPopulationTracker? tracker = null,
        Dictionary<string, object>? configParams = null)
    {
        var mockStore = store ?? Mock.Of<IBrowserFingerprintStore>(s => s.Get(It.IsAny<string>()) == null);
        var options = Options.Create(new BotDetectionOptions
        {
            ClientSide = { Enabled = true }
        });
        var detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            options,
            mockStore);

        return new ClientSideContributor(
            NullLogger<ClientSideContributor>.Instance,
            detector,
            tracker ?? new FingerprintPopulationTracker(),
            new StubConfigProvider(configParams));
    }

    #endregion

    // ─── FingerprintPopulationTracker ─────────────────────────────────────────

    [Fact]
    public void Tracker_NoData_ReturnsZeroRate()
    {
        var tracker = new FingerprintPopulationTracker();
        var (rate, samples) = tracker.GetRate("Chrome", "document");
        Assert.Equal(0.0, rate);
        Assert.Equal(0, samples);
    }

    [Fact]
    public void Tracker_AllWithFingerprint_ReturnsRateOne()
    {
        var tracker = new FingerprintPopulationTracker();
        for (var i = 0; i < 10; i++)
            tracker.Record("Chrome", "document", hasFingerprint: true);

        var (rate, samples) = tracker.GetRate("Chrome", "document");
        Assert.Equal(1.0, rate, precision: 2);
        Assert.Equal(10, samples);
    }

    [Fact]
    public void Tracker_NoneWithFingerprint_ReturnsRateZero()
    {
        var tracker = new FingerprintPopulationTracker();
        for (var i = 0; i < 10; i++)
            tracker.Record("curl", "document", hasFingerprint: false);

        var (rate, samples) = tracker.GetRate("curl", "document");
        Assert.Equal(0.0, rate, precision: 2);
        Assert.Equal(10, samples);
    }

    [Fact]
    public void Tracker_MixedPopulation_CorrectRate()
    {
        var tracker = new FingerprintPopulationTracker();
        for (var i = 0; i < 8; i++)
            tracker.Record("Firefox", "document", hasFingerprint: true);
        for (var i = 0; i < 2; i++)
            tracker.Record("Firefox", "document", hasFingerprint: false);

        var (rate, samples) = tracker.GetRate("Firefox", "document");
        Assert.Equal(0.8, rate, precision: 2);
        Assert.Equal(10, samples);
    }

    [Fact]
    public void Tracker_BucketsAreIsolated()
    {
        var tracker = new FingerprintPopulationTracker();
        tracker.Record("Chrome", "document", hasFingerprint: true);
        tracker.Record("Chrome", "api", hasFingerprint: false);

        var (docRate, _) = tracker.GetRate("Chrome", "document");
        var (apiRate, _) = tracker.GetRate("Chrome", "api");

        Assert.Equal(1.0, docRate, precision: 2);
        Assert.Equal(0.0, apiRate, precision: 2);
    }

    [Fact]
    public void Tracker_SlidingWindow_HalvesAtCapacity()
    {
        const int window = 10;
        var tracker = new FingerprintPopulationTracker(windowSize: window);

        // Fill window with all-fingerprint records
        for (var i = 0; i < window; i++)
            tracker.Record("Chrome", "document", hasFingerprint: true);

        // The (window+1)-th record triggers a halve then add
        // Before: (10, 10). After halve: (5, 5). After add(true): (6, 6). Rate = 1.0
        tracker.Record("Chrome", "document", hasFingerprint: true);
        var (rate, samples) = tracker.GetRate("Chrome", "document");
        Assert.Equal(1.0, rate, precision: 2);
        Assert.Equal(6, samples);
    }

    [Fact]
    public void Tracker_SlidingWindow_Halve_PreservesLowRate()
    {
        const int window = 10;
        var tracker = new FingerprintPopulationTracker(windowSize: window);

        // 2 fingerprint, 8 no-fingerprint
        for (var i = 0; i < 2; i++)
            tracker.Record("Bot", "document", hasFingerprint: true);
        for (var i = 0; i < 8; i++)
            tracker.Record("Bot", "document", hasFingerprint: false);

        // Trigger halve
        tracker.Record("Bot", "document", hasFingerprint: false);
        // Before: (10, 2). After halve: (5, 1). After add(false): (6, 1). Rate ≈ 0.17
        var (rate, samples) = tracker.GetRate("Bot", "document");
        Assert.Equal(6, samples);
        Assert.True(rate < 0.3, $"Expected low rate, got {rate}");
    }

    // ─── ClientSideDetector sentinel ──────────────────────────────────────────

    [Fact]
    public async Task Detector_Disabled_ReturnsEmptyResult()
    {
        var options = Options.Create(new BotDetectionOptions { ClientSide = { Enabled = false } });
        var detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            options,
            Mock.Of<IBrowserFingerprintStore>());

        var ctx = new DefaultHttpContext();
        var result = await detector.DetectAsync(ctx);

        Assert.Empty(result.Reasons);
    }

    [Fact]
    public async Task Detector_NoFingerprint_ReturnsSentinel()
    {
        var options = Options.Create(new BotDetectionOptions { ClientSide = { Enabled = true } });
        var store = Mock.Of<IBrowserFingerprintStore>(s => s.Get(It.IsAny<string>()) == null);
        var detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            options,
            store);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 Chrome/120.0.0.0";
        var result = await detector.DetectAsync(ctx);

        Assert.Single(result.Reasons);
        Assert.Equal("no_fingerprint", result.Reasons[0].Detail);
        Assert.Equal(0.0, result.Reasons[0].ConfidenceImpact, precision: 3);
    }

    // ─── ClientSideContributor adaptive paths ─────────────────────────────────

    [Fact]
    public async Task Contributor_Disabled_NoContribution()
    {
        var options = Options.Create(new BotDetectionOptions { ClientSide = { Enabled = false } });
        var detector = new ClientSideDetector(
            NullLogger<ClientSideDetector>.Instance,
            options,
            Mock.Of<IBrowserFingerprintStore>());
        var contributor = new ClientSideContributor(
            NullLogger<ClientSideContributor>.Instance,
            detector,
            new FingerprintPopulationTracker(),
            new StubConfigProvider());

        var state = CreateState();
        var result = await contributor.ContributeAsync(state);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_NonDocument_NoContribution()
    {
        var state = CreateState(transportClass: "api");
        var contributor = CreateContributor();
        var result = await contributor.ContributeAsync(state);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_SignalR_NoContribution()
    {
        var state = CreateState(transportClass: "signalr");
        var contributor = CreateContributor();
        var result = await contributor.ContributeAsync(state);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_Static_NoContribution()
    {
        var state = CreateState(transportClass: "static");
        var contributor = CreateContributor();
        var result = await contributor.ContributeAsync(state);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_Document_InsufficientData_HalfPenalty()
    {
        // No population data yet - should apply half of penalize_confidence
        const double penaltyBase = 0.15;
        var config = new Dictionary<string, object>
        {
            ["penalize_no_fingerprint"] = true,
            ["penalize_confidence"] = penaltyBase,
            ["population_min_samples"] = 20,
            ["population_rate_threshold"] = 0.7
        };
        var state = CreateState(transportClass: "document", uaFamily: "Chrome");
        var contributor = CreateContributor(configParams: config);
        var result = await contributor.ContributeAsync(state);

        Assert.Single(result);
        Assert.Equal(penaltyBase * 0.5, result[0].ConfidenceDelta, precision: 4);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_Document_HighPopulationRate_FullScaledPenalty()
    {
        const double penaltyBase = 0.15;
        const double threshold = 0.7;
        var config = new Dictionary<string, object>
        {
            ["penalize_no_fingerprint"] = true,
            ["penalize_confidence"] = penaltyBase,
            ["population_min_samples"] = 5,
            ["population_rate_threshold"] = threshold
        };

        var tracker = new FingerprintPopulationTracker();
        // Seed: 9 out of 10 sent fingerprints → rate = 0.9
        for (var i = 0; i < 9; i++)
            tracker.Record("Chrome", "document", hasFingerprint: true);
        tracker.Record("Chrome", "document", hasFingerprint: false);

        var state = CreateState(transportClass: "document", uaFamily: "Chrome");
        var contributor = CreateContributor(tracker: tracker, configParams: config);
        var result = await contributor.ContributeAsync(state);

        // After recording the current no-fp request: (11 total, 9 fp) → rate = 9/11 ≈ 0.818
        Assert.Single(result);
        var bias = result[0].ConfidenceDelta;
        Assert.True(bias > penaltyBase * threshold, $"Bias {bias} should be > {penaltyBase * threshold}");
        Assert.True(bias <= penaltyBase, $"Bias {bias} should be <= penaltyBase {penaltyBase}");
    }

    [Fact]
    public async Task Contributor_NoFingerprint_Document_LowPopulationRate_NoPenalty()
    {
        // UA family where fingerprints are rarely seen - no penalty should apply
        const double threshold = 0.7;
        var config = new Dictionary<string, object>
        {
            ["penalize_no_fingerprint"] = true,
            ["penalize_confidence"] = 0.15,
            ["population_min_samples"] = 5,
            ["population_rate_threshold"] = threshold
        };

        var tracker = new FingerprintPopulationTracker();
        // 1 out of 10 sends a fingerprint → rate = 0.1 (below threshold)
        tracker.Record("curl-bot", "document", hasFingerprint: true);
        for (var i = 0; i < 9; i++)
            tracker.Record("curl-bot", "document", hasFingerprint: false);

        var state = CreateState(transportClass: "document", uaFamily: "curl-bot");
        var contributor = CreateContributor(tracker: tracker, configParams: config);
        var result = await contributor.ContributeAsync(state);

        // Low rate = this UA family doesn't normally send fingerprints → no penalty
        Assert.Empty(result);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_Document_PenalizeDisabled_NoContribution()
    {
        var config = new Dictionary<string, object>
        {
            ["penalize_no_fingerprint"] = false
        };
        var state = CreateState(transportClass: "document");
        var contributor = CreateContributor(configParams: config);
        var result = await contributor.ContributeAsync(state);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Contributor_NoFingerprint_RecordsPopulation_AsNotSent()
    {
        var tracker = new FingerprintPopulationTracker();
        var config = new Dictionary<string, object>
        {
            ["penalize_no_fingerprint"] = true,
            ["penalize_confidence"] = 0.15,
            ["population_min_samples"] = 100, // high - so we won't get a penalty
            ["population_rate_threshold"] = 0.7
        };
        var state = CreateState(transportClass: "document", uaFamily: "Safari");
        var contributor = CreateContributor(tracker: tracker, configParams: config);

        await contributor.ContributeAsync(state);

        // Population should have recorded this as no-fingerprint
        var (rate, samples) = tracker.GetRate("Safari", "document");
        Assert.Equal(1, samples);
        Assert.Equal(0.0, rate, precision: 2);
    }

    [Fact]
    public async Task Contributor_FingerprintFound_RecordsPopulation_AsSent()
    {
        // Arrange: store returns a fingerprint result (clean browser)
        var fp = new BrowserFingerprintResult
        {
            HeadlessLikelihood = 0.0,
            IsHeadless = false,
            BrowserIntegrityScore = 100,
            FingerprintConsistencyScore = 100,
            FingerprintHash = "abc123",
            ProcessedAt = DateTimeOffset.UtcNow,
            RequestId = "req1"
        };
        var store = Mock.Of<IBrowserFingerprintStore>(s => s.Get(It.IsAny<string>()) == fp);

        var tracker = new FingerprintPopulationTracker();
        var state = CreateState(transportClass: "document", uaFamily: "Chrome");
        var contributor = CreateContributor(store: store, tracker: tracker);

        await contributor.ContributeAsync(state);

        // Should have recorded fingerprint as present
        var (rate, samples) = tracker.GetRate("Chrome", "document");
        Assert.Equal(1, samples);
        Assert.Equal(1.0, rate, precision: 2);
    }

    [Fact]
    public async Task Contributor_FingerprintFound_ApiRequest_PopulationNotRecorded()
    {
        var fp = new BrowserFingerprintResult
        {
            HeadlessLikelihood = 0.0,
            IsHeadless = false,
            BrowserIntegrityScore = 100,
            FingerprintConsistencyScore = 100,
            FingerprintHash = "abc123",
            ProcessedAt = DateTimeOffset.UtcNow,
            RequestId = "req1"
        };
        var store = Mock.Of<IBrowserFingerprintStore>(s => s.Get(It.IsAny<string>()) == fp);

        var tracker = new FingerprintPopulationTracker();
        var state = CreateState(transportClass: "api", uaFamily: "Chrome");
        var contributor = CreateContributor(store: store, tracker: tracker);

        await contributor.ContributeAsync(state);

        // API requests should NOT be recorded in population (they never run the fingerprint script)
        var (_, samples) = tracker.GetRate("Chrome", "api");
        Assert.Equal(0, samples);
    }

    [Fact]
    public async Task Contributor_NoFingerprintBias_WrittenToSignals()
    {
        const double penaltyBase = 0.15;
        var config = new Dictionary<string, object>
        {
            ["penalize_no_fingerprint"] = true,
            ["penalize_confidence"] = penaltyBase,
            ["population_min_samples"] = 100 // force half-penalty path
        };
        var state = CreateState(transportClass: "document");
        var contributor = CreateContributor(configParams: config);

        await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey(SignalKeys.ClientSideNoFingerprintBias));
        var bias = (double)state.Signals[SignalKeys.ClientSideNoFingerprintBias];
        Assert.Equal(penaltyBase * 0.5, bias, precision: 4);
    }
}
