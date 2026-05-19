using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins the risk.* signal surface emitted by IdentityChangeContributor.
///     This is the FOSS stub for the commercial API-protection feature; tests
///     defend the contract so the dashboard and downstream policy can rely on
///     it being there.
/// </summary>
public class IdentityChangeContributorTests
{
    private static IdentityChangeContributor BuildContributor(FingerprintDimSnapshotCache cache)
        => new(
            NullLogger<IdentityChangeContributor>.Instance,
            new StubConfigProvider(),
            cache);

    private static BlackboardState NewState(
        string fingerprintId,
        string country,
        string asn,
        string uaFamily,
        bool isDatacenter = false,
        bool isVpn = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        var signals = new ConcurrentDictionary<string, object>
        {
            [SignalKeys.IdentityFingerprintId] = fingerprintId,
            [SignalKeys.GeoCountryCode] = country,
            [SignalKeys.IpAsn] = asn,
            [SignalKeys.UserAgentFamily] = uaFamily,
            [SignalKeys.IpIsDatacenter] = isDatacenter,
            [SignalKeys.GeoIsVpn] = isVpn
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
    public async Task FirstSighting_NoRiskSignalsEmitted()
    {
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        var state = NewState("fp-1", "US", "AS15169", "Chrome");
        var contributions = await c.ContributeAsync(state, default);

        Assert.Empty(contributions);
        Assert.False(state.Signals.ContainsKey(SignalKeys.RiskCountryChanged));
        Assert.False(state.Signals.ContainsKey(SignalKeys.RiskSuspiciousChangeScore));
    }

    [Fact]
    public async Task MissingFingerprintId_NoContribution()
    {
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        var state = NewState("", "US", "AS15169", "Chrome");
        var contributions = await c.ContributeAsync(state, default);

        Assert.Empty(contributions);
    }

    [Fact]
    public async Task CountryChange_EmitsTransitionAndScore()
    {
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        // Establish baseline
        await c.ContributeAsync(NewState("fp-1", "US", "AS15169", "Chrome"), default);

        // Shift country
        var state2 = NewState("fp-1", "RU", "AS15169", "Chrome");
        var contributions = await c.ContributeAsync(state2, default);

        Assert.Single(contributions);
        Assert.True((bool)state2.Signals[SignalKeys.RiskCountryChanged]);
        Assert.Equal("US -> RU", state2.Signals[SignalKeys.RiskCountryTransition]);
        Assert.True((double)state2.Signals[SignalKeys.RiskSuspiciousChangeScore] > 0);
        Assert.Contains("US -> RU", (string)state2.Signals[SignalKeys.RiskSuspiciousChangeReason]);
    }

    [Fact]
    public async Task EverythingChanged_ScoreClampedToOne()
    {
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        await c.ContributeAsync(NewState("fp-1", "US", "AS15169", "Chrome"), default);

        // Country + ASN + UA family + datacenter all flip at once
        var state2 = NewState("fp-1", "RU", "AS9009", "python-requests", isDatacenter: true);
        await c.ContributeAsync(state2, default);

        var score = (double)state2.Signals[SignalKeys.RiskSuspiciousChangeScore];
        Assert.True(score <= 1.0);
        Assert.True(score > 0.5, $"All-dims-changed should produce strong score, got {score}");
        Assert.True((bool)state2.Signals[SignalKeys.RiskInfrastructureIntroduced]);
        Assert.True((bool)state2.Signals[SignalKeys.RiskUaFamilyChanged]);
        Assert.True((bool)state2.Signals[SignalKeys.RiskAsnChanged]);
    }

    [Fact]
    public async Task StableFingerprint_NoSecondAlert()
    {
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        await c.ContributeAsync(NewState("fp-1", "US", "AS15169", "Chrome"), default);
        await c.ContributeAsync(NewState("fp-1", "US", "AS15169", "Chrome"), default);
        var state3 = NewState("fp-1", "US", "AS15169", "Chrome");
        var contributions = await c.ContributeAsync(state3, default);

        Assert.Empty(contributions);
        Assert.False(state3.Signals.ContainsKey(SignalKeys.RiskCountryChanged));
    }

    [Fact]
    public async Task TransitionBecomesNewBaseline_NoRefireOnSameNewState()
    {
        // After country flips US -> RU, a third request still in RU must NOT refire.
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        await c.ContributeAsync(NewState("fp-1", "US", "AS15169", "Chrome"), default);
        await c.ContributeAsync(NewState("fp-1", "RU", "AS15169", "Chrome"), default);
        var state3 = NewState("fp-1", "RU", "AS15169", "Chrome");
        var contributions = await c.ContributeAsync(state3, default);

        Assert.Empty(contributions);
    }

    [Fact]
    public async Task IsolatedFingerprints_DoNotShareBaseline()
    {
        // fp-A baseline in US; fp-B's first request in RU must be treated as
        // a first-sighting (not a country-change) - cache is keyed on fp id.
        var cache = new FingerprintDimSnapshotCache();
        var c = BuildContributor(cache);

        await c.ContributeAsync(NewState("fp-A", "US", "AS15169", "Chrome"), default);
        var stateB = NewState("fp-B", "RU", "AS9009", "Firefox");
        var contributions = await c.ContributeAsync(stateB, default);

        Assert.Empty(contributions);
        Assert.False(stateB.Signals.ContainsKey(SignalKeys.RiskCountryChanged));
    }

    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        public DetectorManifest? GetManifest(string detectorName) => null;
        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 0.5, HumanSignal = 0.0 },
            Confidence = new ConfidenceDefaults { BotDetected = 0.2 },
            Parameters = new Dictionary<string, object>
            {
                ["country_change_weight"] = 0.35,
                ["asn_change_weight"] = 0.20,
                ["ua_family_change_weight"] = 0.30,
                ["infra_introduced_weight"] = 0.25,
                ["contribution_confidence"] = 0.2
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
