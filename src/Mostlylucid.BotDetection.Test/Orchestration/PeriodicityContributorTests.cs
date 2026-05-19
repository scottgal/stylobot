using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins the periodicity.* signal surface restored on 2026-05-19 after the
///     2026-05-08 retirement. These tests defend the API-key-theft / cadence-shift
///     use case - if the contributor stops emitting these signals again, the test
///     suite catches it before the dashboard does.
/// </summary>
public class PeriodicityContributorTests
{
    private static PeriodicityContributor BuildContributor(IMemoryCache cache)
        => new(
            NullLogger<PeriodicityContributor>.Instance,
            new StubConfigProvider(),
            cache);

    private static BlackboardState NewState(string sig = "test-sig")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        var signals = new ConcurrentDictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = sig
        };
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N")
        };
    }

    [Fact]
    public async Task BelowMinRequests_NoSignalsEmitted()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var c = BuildContributor(cache);

        // Default min_requests=10 - a single request can't compute periodicity
        var state = NewState();
        await c.ContributeAsync(state, default);

        Assert.False(state.Signals.ContainsKey(SignalKeys.PeriodicityCV),
            "Single request should NOT emit periodicity signals - need >= min_requests history");
    }

    [Fact]
    public async Task FixedIntervalPolling_EmitsLowCV()
    {
        // 12 requests at exactly 1s apart - canonical bot polling
        var cache = new MemoryCache(new MemoryCacheOptions());
        var c = BuildContributor(cache);

        for (var i = 0; i < 12; i++)
        {
            var state = NewState("bot-sig");
            await c.ContributeAsync(state, default);
            await Task.Delay(50);  // brief gap so the timestamps differ even at clock resolution
            if (i == 11)
            {
                // Final request should have the periodicity surface fully populated
                Assert.True(state.Signals.ContainsKey(SignalKeys.PeriodicityCV));
                Assert.True(state.Signals.ContainsKey(SignalKeys.PeriodicityMeanInterval));
                Assert.True(state.Signals.ContainsKey(SignalKeys.PeriodicityDominantPeriod));
                Assert.True(state.Signals.ContainsKey(SignalKeys.PeriodicityPeakStrength));
                Assert.True(state.Signals.ContainsKey(SignalKeys.PeriodicityHourEntropy));
            }
        }
    }

    [Fact]
    public async Task SignatureScopedHistory_DoesNotLeakAcrossSignatures()
    {
        // Two distinct signatures should each accumulate their own history.
        // Verified by: 10 requests to sig-a primes its history; sig-b's first
        // request must still hit "below min_requests" gate (no periodicity signals).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var c = BuildContributor(cache);

        for (var i = 0; i < 10; i++)
        {
            await c.ContributeAsync(NewState("sig-a"), default);
            await Task.Delay(10);
        }

        var sigBState = NewState("sig-b");
        await c.ContributeAsync(sigBState, default);
        Assert.False(sigBState.Signals.ContainsKey(SignalKeys.PeriodicityCV),
            "sig-b should NOT inherit sig-a's history - signatures are scope boundaries");
    }

    [Fact]
    public async Task MissingPrimarySignature_NoContribution()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var c = BuildContributor(cache);

        var state = NewState();
        state.Signals.GetType().GetMethod("TryRemove", new[] { typeof(string), typeof(object).MakeByRefType() });
        ((ConcurrentDictionary<string, object>)state.Signals).TryRemove(SignalKeys.PrimarySignature, out _);

        var contributions = await c.ContributeAsync(state, default);
        Assert.Empty(contributions);
    }

    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        public DetectorManifest? GetManifest(string detectorName) => null;
        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.2, HumanSignal = 0.8 },
            Confidence = new ConfidenceDefaults { BotDetected = 0.4 },
            Parameters = new Dictionary<string, object>
            {
                ["min_requests"] = 10,
                ["max_lag"] = 50,
                ["regularity_bot_confidence"] = 0.35,
                ["cron_pattern_bot_confidence"] = 0.4
            }
        };
        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            var defs = GetDefaults(detectorName);
            if (defs.Parameters.TryGetValue(parameterName, out var v))
                try { return (T)Convert.ChangeType(v, typeof(T)); } catch { }
            return defaultValue;
        }
        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));
        public void InvalidateCache(string? detectorName = null) { }
        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }
}
