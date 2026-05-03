using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Unit tests for <see cref="ClickFraudContributor" />.
///     Covers: datacenter + paid traffic scoring, organic/residential baseline,
///     referrer spoofing pattern, immediate bounce, headless + paid, gate signals.
/// </summary>
public class ClickFraudContributorTests
{
    #region Infrastructure

    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _parameters;

        public StubConfigProvider(Dictionary<string, object>? parameters = null)
            => _parameters = parameters ?? new Dictionary<string, object>();

        public DetectorManifest? GetManifest(string detectorName) => null;

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
            Confidence = new ConfidenceDefaults
                { BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5 },
            Parameters = new Dictionary<string, object>(_parameters)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_parameters.TryGetValue(parameterName, out var val))
            {
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        public Task<T> GetParameterAsync<T>(
            string detectorName, string parameterName, ConfigResolutionContext context,
            T defaultValue, CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private static ClickFraudContributor CreateContributor(Dictionary<string, object>? configParams = null)
        => new(
            NullLogger<ClickFraudContributor>.Instance,
            new StubConfigProvider(configParams));

    private static BlackboardState CreateState(Dictionary<string, object>? signals = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Request.Headers.UserAgent = "Mozilla/5.0";

        var dict = new ConcurrentDictionary<string, object>(
            signals ?? new Dictionary<string, object>());

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

    /// <summary>
    ///     Datacenter IP + paid traffic (utm.present + utm.has_gclid) + referrer mismatch
    ///     should combine to produce a score above the default 0.55 threshold and yield
    ///     a positive-confidence (bot) contribution.
    /// </summary>
    [Fact]
    public async Task Contribute_DatacenterAndPaid_ScoreAboveThreshold()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.UtmPresent] = true,
            [SignalKeys.UtmHasGclid] = true,
            [SignalKeys.IpIsDatacenter] = true,
            [SignalKeys.UtmReferrerMismatch] = true
        });

        var contributions = await contributor.ContributeAsync(state, CancellationToken.None);

        var confidence = state.GetSignal<double>(SignalKeys.ClickFraudConfidence);
        Assert.True(confidence >= 0.55,
            $"Expected clickfraud.confidence >= 0.55 but was {confidence:F2}");

        Assert.Single(contributions);
        Assert.True(contributions[0].ConfidenceDelta > 0,
            "Contribution ConfidenceDelta should be positive for fraud-flagged request");
    }

    /// <summary>
    ///     Organic residential traffic with no UTM/click-ID/datacenter/headless signals
    ///     should score below the threshold and produce a neutral contribution.
    /// </summary>
    [Fact]
    public async Task Contribute_OrganicResidential_NearZeroScore()
    {
        var contributor = CreateContributor();
        // No fraud signals whatsoever - but also no session.request_count or ip.is_datacenter,
        // so trigger conditions would need utm.present. Provide utm.present=false
        // by simply not adding it (defaults to false via GetSignal).
        var state = CreateState();

        // We need at least one trigger signal to get through TriggerConditions.
        // Manually add utm.present = true but with none of the fraud factors.
        state.WriteSignal(SignalKeys.UtmPresent, true);

        var contributions = await contributor.ContributeAsync(state, CancellationToken.None);

        var confidence = state.GetSignal<double>(SignalKeys.ClickFraudConfidence);
        Assert.True(confidence < 0.55,
            $"Expected clickfraud.confidence < 0.55 for organic residential but was {confidence:F2}");

        Assert.Single(contributions);
        // Neutral contribution has ConfidenceDelta <= 0
        Assert.True(contributions[0].ConfidenceDelta <= 0,
            "Organic residential traffic should yield a neutral (non-positive) contribution");
    }

    /// <summary>
    ///     When utm.present + utm.has_gclid + utm.referrer_mismatch are set,
    ///     clickfraud.pattern should contain "referrer_spoof".
    /// </summary>
    [Fact]
    public async Task Contribute_ReferrerSpoofWithClickId_AddsPattern()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.UtmPresent] = true,
            [SignalKeys.UtmHasGclid] = true,
            [SignalKeys.UtmReferrerMismatch] = true
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.ClickFraudPattern),
            "clickfraud.pattern should be written for referrer spoof scenario");

        var pattern = state.GetSignal<string>(SignalKeys.ClickFraudPattern);
        Assert.Contains("referrer_spoof", pattern,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     session.request_count == 1 combined with utm.present should produce
    ///     the "immediate_bounce" pattern (single-page session).
    /// </summary>
    [Fact]
    public async Task Contribute_SinglePage_AddsImmediateBouncePattern()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.UtmPresent] = true,
            [SignalKeys.SessionRequestCount] = 1
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.ClickFraudPattern),
            "clickfraud.pattern should be written when single-page session detected");

        var pattern = state.GetSignal<string>(SignalKeys.ClickFraudPattern);
        Assert.Contains("immediate_bounce", pattern,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     fingerprint.headless_score > 0.5 combined with utm.present + utm.has_gclid
    ///     should produce the "headless_paid" pattern.
    /// </summary>
    [Fact]
    public async Task Contribute_HeadlessAndPaid_AddsHeadlessPaidPattern()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.UtmPresent] = true,
            [SignalKeys.FingerprintHeadlessScore] = 0.9,
            [SignalKeys.UtmHasGclid] = true
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.ClickFraudPattern),
            "clickfraud.pattern should be written when headless + paid traffic detected");

        var pattern = state.GetSignal<string>(SignalKeys.ClickFraudPattern);
        Assert.Contains("headless_paid", pattern,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     clickfraud.checked must always be written to true regardless of any signals,
    ///     so downstream detectors can use it as a gate signal.
    /// </summary>
    [Fact]
    public async Task Contribute_CheckedSignalAlwaysWritten()
    {
        var contributor = CreateContributor();

        // Run with utm.present only (minimal trigger)
        var stateWithUtm = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.UtmPresent] = true
        });
        await contributor.ContributeAsync(stateWithUtm, CancellationToken.None);
        Assert.True(stateWithUtm.Signals.ContainsKey(SignalKeys.ClickFraudChecked),
            "clickfraud.checked must be written when utm.present is set");
        Assert.True(stateWithUtm.GetSignal<bool>(SignalKeys.ClickFraudChecked));

        // Run with datacenter + session.request_count trigger path (no utm.present)
        var stateDatacenter = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.IpIsDatacenter] = true,
            [SignalKeys.SessionRequestCount] = 5
        });
        await contributor.ContributeAsync(stateDatacenter, CancellationToken.None);
        Assert.True(stateDatacenter.Signals.ContainsKey(SignalKeys.ClickFraudChecked),
            "clickfraud.checked must be written when datacenter+session trigger path fires");
        Assert.True(stateDatacenter.GetSignal<bool>(SignalKeys.ClickFraudChecked));
    }

    /// <summary>
    ///     clickfraud.is_paid_traffic is true only when utm.present AND at least one click-ID
    ///     (gclid/fbclid/msclkid/ttclid) is present; it must be false when no UTM signals exist.
    /// </summary>
    [Fact]
    public async Task Contribute_IsPaidTrafficSignalCorrect()
    {
        var contributor = CreateContributor();

        // Paid: utm.present + has_gclid
        var paidState = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.UtmPresent] = true,
            [SignalKeys.UtmHasGclid] = true
        });
        await contributor.ContributeAsync(paidState, CancellationToken.None);
        Assert.True(paidState.Signals.ContainsKey(SignalKeys.ClickFraudIsPaidTraffic),
            "clickfraud.is_paid_traffic must be written");
        Assert.True(paidState.GetSignal<bool>(SignalKeys.ClickFraudIsPaidTraffic),
            "clickfraud.is_paid_traffic must be true when utm.present + click-ID are present");

        // Non-paid: no UTM signals (trigger via datacenter + session.request_count)
        var organicState = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.IpIsDatacenter] = true,
            [SignalKeys.SessionRequestCount] = 3
        });
        await contributor.ContributeAsync(organicState, CancellationToken.None);
        Assert.True(organicState.Signals.ContainsKey(SignalKeys.ClickFraudIsPaidTraffic),
            "clickfraud.is_paid_traffic must always be written after contributor runs");
        Assert.False(organicState.GetSignal<bool>(SignalKeys.ClickFraudIsPaidTraffic),
            "clickfraud.is_paid_traffic must be false when no UTM signals present");
    }
}
