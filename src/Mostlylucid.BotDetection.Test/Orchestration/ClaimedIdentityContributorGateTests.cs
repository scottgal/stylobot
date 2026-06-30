using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins the closed-loop feedback gate on
///     <see cref="ClaimedIdentityContributor.ShouldUpdateCentroid"/> (audit
///     #6 + <c>project_centroid_learning_feedback_loop</c>). UA centroids
///     drift slowly via EWM (alpha=0.99, ~100 samples to drift 50%) so a
///     single hour of shed / throttle / block traffic biases the prior for
///     days. The gate must refuse to update on ANY of:
///       * stylobot synthesised the response (signals[response.from_upstream]=false)
///       * upstream is unhealthy (UpstreamHealthGate.IsUpstreamHealthy()=false)
///       * gateway is in cold-start warmup (GatewayWarmupGate.IsWarmedUp()=false)
///       * the request was load-shed (HttpContext.Items[BotDetectionShedKey])
///     while a "natural" request (all four axes clear) still walks the EWM.
/// </summary>
public class ClaimedIdentityContributorGateTests
{
    private static LiveCentroid BuildCentroid(string tier = "browser") => new()
    {
        Family = "chrome",
        Tier = tier,
        Dimensions = new Dictionary<string, LiveDimension>
        {
            ["sec_fetch_present"] = new() { Mean = 1.0, Weight = 1.0 },
            ["accept_html"] = new() { Mean = 1.0, Weight = 1.0 }
        },
        SampleCount = 0
    };

    private static ClaimedIdentityContributor BuildContributor(
        UpstreamHealthGate? upstreamHealth = null,
        GatewayWarmupGate? gatewayWarmup = null)
    {
        // UaProfileStore loads YAML from embedded resources on construction;
        // the gate test never asks the store to resolve anything (we hand it
        // a synthetic LiveCentroid) so the seed YAML is irrelevant. Default
        // constructor is the smallest dependency-surface that compiles.
        var profileStore = new UaProfileStore(
            NullLogger<UaProfileStore>.Instance);

        return new ClaimedIdentityContributor(
            NullLogger<ClaimedIdentityContributor>.Instance,
            new NullDetectorConfigProvider(),
            profileStore,
            upstreamHealth,
            gatewayWarmup);
    }

    private static BlackboardState BuildState(
        Dictionary<string, object>? signals = null,
        Action<DefaultHttpContext>? configureCtx = null)
    {
        var ctx = new DefaultHttpContext();
        configureCtx?.Invoke(ctx);
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

    [Fact]
    public void Natural_request_with_no_gates_wired_updates_centroid()
    {
        // Regression guardrail: existing FOSS hosts without UpstreamHealthGate
        // / GatewayWarmupGate (both optional) must behave exactly as before
        // the gate landed. Browser tier + residential IP + healthy upstream
        // + no shed flag => update fires.
        var contributor = BuildContributor();
        var centroid = BuildCentroid();
        var state = BuildState(); // no signals at all

        Assert.True(contributor.ShouldUpdateCentroid(centroid, 0.95, state));
    }

    [Fact]
    public void Refuses_when_response_from_upstream_is_false()
    {
        // Stylobot synthesised the response (load-shed 503, policy block 403,
        // throttle 429, honeypot 404, API-key reject). Observed behavioural
        // dimensions are policy-shaped, not a fair sample of the UA's
        // genuine behaviour.
        var contributor = BuildContributor();
        var centroid = BuildCentroid();
        var state = BuildState(new Dictionary<string, object>
        {
            [SignalKeys.ResponseFromUpstream] = (bool?)false
        });

        Assert.False(contributor.ShouldUpdateCentroid(centroid, 0.95, state));
    }

    [Fact]
    public void Refuses_when_upstream_health_gate_reports_unhealthy()
    {
        // Origin-down / cold-start window: behavioural arms read shape from
        // the outage, not the UA family's genuine behaviour. UpstreamHealthGate
        // is queried directly so the gate composes with the existing
        // U2 status-derived suppression.
        var atom = new DegradationAtom();
        // Drive 5xx EMA past the gate's threshold so IsUpstreamHealthy()=false.
        for (var i = 0; i < 200; i++)
            atom.RecordResponse(500, latencyMs: 25, path: "/");
        var unhealthyGate = new UpstreamHealthGate(
            atom, Options.Create(new UpstreamHealthOptions()));

        var contributor = BuildContributor(upstreamHealth: unhealthyGate);
        var centroid = BuildCentroid();
        var state = BuildState();

        Assert.False(contributor.ShouldUpdateCentroid(centroid, 0.95, state));
    }

    [Fact]
    public void Refuses_when_gateway_warmup_gate_reports_warming()
    {
        // Gateway is in cold-start warmup: behavioural classifiers haven't
        // accumulated enough samples to be reliable; persisting noisy
        // verdicts into the slow-decay centroid wastes days of drift.
        var atom = new DegradationAtom();
        // Fresh atom => total samples below MinGatewaySamples => warming=true.
        var warmupGate = new GatewayWarmupGate(
            atom,
            Options.Create(new GatewayWarmupOptions
            {
                WarmupDuration = TimeSpan.FromMinutes(3),
                MinGatewaySamples = 200,
                MinSignatureSamples = 8
            }));

        var contributor = BuildContributor(gatewayWarmup: warmupGate);
        var centroid = BuildCentroid();
        var state = BuildState();

        Assert.False(contributor.ShouldUpdateCentroid(centroid, 0.95, state));
    }

    [Fact]
    public void Refuses_when_request_was_load_shed()
    {
        // Defence in depth: normally the orchestrator short-circuits before
        // contributors run for shed requests, so the shed marker rarely
        // reaches this code path. The gate refuses unconditionally so
        // future codepaths (commercial pack with custom shed dispatch, etc.)
        // can't sneak shed-shaped samples into the prior.
        var contributor = BuildContributor();
        var centroid = BuildCentroid();
        var state = BuildState(configureCtx: ctx =>
            ctx.Items[BotDetectionMiddleware.BotDetectionShedKey] = true);

        Assert.False(contributor.ShouldUpdateCentroid(centroid, 0.95, state));
    }

    [Fact]
    public void Existing_consistency_floor_still_applies()
    {
        // Pre-existing behaviour: even a perfectly clean envelope must refuse
        // an update when consistency score is below the 0.65 floor. The
        // new gates are additive; they don't lift the consistency floor.
        var contributor = BuildContributor();
        var centroid = BuildCentroid();
        var state = BuildState();

        Assert.False(contributor.ShouldUpdateCentroid(centroid, 0.50, state));
    }

    [Fact]
    public void Existing_browser_tier_dc_ip_guard_still_applies()
    {
        // Pre-existing behaviour: browser-tier centroids refuse updates from
        // datacenter IPs (the UA family says Chrome but the network shape
        // is server-room) regardless of envelope health. Composes with the
        // new gates; doesn't get superseded by them.
        var contributor = BuildContributor();
        var centroid = BuildCentroid(tier: "browser");
        var state = BuildState(new Dictionary<string, object>
        {
            [SignalKeys.IpIsDatacenter] = true
        });

        Assert.False(contributor.ShouldUpdateCentroid(centroid, 0.95, state));
    }
}

/// <summary>
///     Minimal no-op config provider so the contributor can be instantiated
///     without a full DI container. Mirrors the pattern from
///     <c>UserAgentContributorWellKnownBotsTests</c>.
/// </summary>
file sealed class NullDetectorConfigProvider : IDetectorConfigProvider
{
    public DetectorManifest? GetManifest(string detectorName) => null;

    public DetectorDefaults GetDefaults(string detectorName) => new()
    {
        Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
        Confidence = new ConfidenceDefaults { BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5 },
        Parameters = new Dictionary<string, object>()
    };

    public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;

    public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
        ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
        => Task.FromResult(defaultValue);

    public void InvalidateCache(string? detectorName = null) { }

    public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests() =>
        new Dictionary<string, DetectorManifest>();
}
