using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

/// <summary>
///     In-process coverage for the BDF-style signal contract on the threat-intel
///     contributor. Drives realistic blackboard states through ContributeAsync and
///     asserts on the exact <c>threatintel.*</c> / <c>intel.*</c> / <c>endpoint.*</c>
///     signals that BdfReplayEndpoints now probes, so a regression here surfaces
///     before the slower integration replay runs.
/// </summary>
public class ThreatIntelContributorTests
{
    private static ThreatIntelContributor BuildContributor(
        bool enabled,
        params IThreatIntelProvider[] providers)
    {
        var opts = Options.Create(new BotDetectionOptions
        {
            ThreatIntel = new ThreatIntelOptions { Enabled = enabled }
        });
        var coordinator = new ThreatIntelCoordinator(
            opts, providers, NullLogger<ThreatIntelCoordinator>.Instance);
        return new ThreatIntelContributor(
            NullLogger<ThreatIntelContributor>.Instance,
            new StubConfigProvider(),
            coordinator);
    }

    private static BlackboardState NewState(string ip = "203.0.113.5", string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        var signals = new ConcurrentDictionary<string, object>
        {
            [SignalKeys.ClientIp] = ip
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

    // === Disabled-state: short-circuit, no signals, no contributions ===

    [Fact]
    public async Task Contribute_Disabled_WritesNoSignalsAndNoContributions()
    {
        var c = BuildContributor(enabled: false, new StubProvider("p1", IntelligenceSignalClass.Reputation));
        var state = NewState(path: "/admin");   // even on a sensitive endpoint, disabled = no work

        var contributions = await c.ContributeAsync(state, default);

        Assert.Empty(contributions);
        Assert.False(state.Signals.ContainsKey(SignalKeys.EndpointRisk),
            "Disabled state should NOT write endpoint.risk - regression-guards the IsEnabled short-circuit");
        Assert.False(state.Signals.ContainsKey(SignalKeys.ThreatIntelScore));
        Assert.False(state.Signals.ContainsKey(SignalKeys.IntelHardGate));
    }

    // === Enabled-state: signal emission ===

    [Fact]
    public async Task Contribute_Enabled_WritesEndpointRiskOnEveryRequest()
    {
        var c = BuildContributor(enabled: true, new StubProvider("p1", IntelligenceSignalClass.Unknown));
        var state = NewState(path: "/blog/post");

        await c.ContributeAsync(state, default);

        Assert.True(state.Signals.ContainsKey(SignalKeys.EndpointRisk));
        Assert.Equal("Normal", state.Signals[SignalKeys.EndpointRisk]);
        Assert.True(state.Signals.ContainsKey(SignalKeys.EndpointRiskSensitive));
        Assert.False((bool)state.Signals[SignalKeys.EndpointRiskSensitive]);
    }

    [Fact]
    public async Task Contribute_NoVerdict_StillEmitsEndpointRiskButNoIntelSignals()
    {
        // Provider supports Ip but TryLookup returns null - typical cold-cache live
        // provider state. Endpoint signals still land; intel.* signals don't.
        var c = BuildContributor(enabled: true, new StubProvider("p1", IntelligenceSignalClass.Unknown) { ReturnNull = true });
        var state = NewState(path: "/");

        var contributions = await c.ContributeAsync(state, default);

        Assert.Empty(contributions);
        Assert.True(state.Signals.ContainsKey(SignalKeys.EndpointRisk));
        Assert.False(state.Signals.ContainsKey(SignalKeys.ThreatIntelScore));
        Assert.False(state.Signals.ContainsKey(SignalKeys.IntelClasses));
    }

    // === Intelligence-class signals + wire-string formatting ===

    [Fact]
    public async Task Contribute_VulnerabilityVerdict_EmitsWireStringClass()
    {
        var c = BuildContributor(enabled: true, new StubProvider("cisa-kev", IntelligenceSignalClass.Vulnerability));
        var state = NewState(path: "/blog/post");

        await c.ContributeAsync(state, default);

        Assert.True(state.Signals.ContainsKey(SignalKeys.IntelClasses));
        Assert.Contains("vulnerability", (string)state.Signals[SignalKeys.IntelClasses]);
    }

    [Fact]
    public async Task Contribute_MultipleVerdicts_DedupesClassesInSignal()
    {
        // Two providers, same intelligence class. The intel.classes signal should
        // dedupe so policy doesn't see "SuspiciousNetworkRange;SuspiciousNetworkRange".
        var c = BuildContributor(enabled: true,
            new StubProvider("spamhaus-drop", IntelligenceSignalClass.SuspiciousNetworkRange),
            new StubProvider("spamhaus-edrop", IntelligenceSignalClass.SuspiciousNetworkRange));
        var state = NewState(path: "/");

        await c.ContributeAsync(state, default);

        var classes = (string)state.Signals[SignalKeys.IntelClasses];
        Assert.Equal("suspicious_network_range", classes);  // single value, not doubled
    }

    // === Endpoint-risk modulation ===

    [Fact]
    public async Task Contribute_StaticPath_EmitsSignalsButNoContribution()
    {
        // Static asset endpoints suppress the contribution entirely - the signal
        // surface is still searchable on the dashboard but never bumps probability.
        var c = BuildContributor(enabled: true, new StubProvider("spamhaus-drop", IntelligenceSignalClass.SuspiciousNetworkRange));
        var state = NewState(path: "/static/logo.png");

        var contributions = await c.ContributeAsync(state, default);

        Assert.Empty(contributions);
        Assert.Equal("Static", state.Signals[SignalKeys.EndpointRisk]);
        Assert.True(state.Signals.ContainsKey(SignalKeys.ThreatIntelScore));  // signal still emitted
    }

    [Fact]
    public async Task Contribute_SensitivePath_TriggersHardGate()
    {
        // Non-cloud intel verdict on a sensitive endpoint trips intel.hard_gate so
        // transition rules can fire on a single boolean.
        var c = BuildContributor(enabled: true, new StubProvider("spamhaus-drop", IntelligenceSignalClass.SuspiciousNetworkRange));
        var state = NewState(path: "/admin/login");

        var contributions = await c.ContributeAsync(state, default);

        Assert.Single(contributions);
        Assert.Equal("Sensitive", state.Signals[SignalKeys.EndpointRisk]);
        Assert.True((bool)state.Signals[SignalKeys.IntelHardGate]);
    }

    [Fact]
    public async Task Contribute_CloudInfraOnSensitivePath_DoesNotTriggerHardGate()
    {
        // CloudInfrastructure is identification, not risk - sensitive-endpoint
        // gate should NOT fire on a cloud:aws-only verdict.
        var c = BuildContributor(enabled: true, new StubProvider("cloud-ranges", IntelligenceSignalClass.CloudInfrastructure));
        var state = NewState(path: "/admin");

        await c.ContributeAsync(state, default);

        Assert.Equal("Sensitive", state.Signals[SignalKeys.EndpointRisk]);
        Assert.False((bool)state.Signals[SignalKeys.IntelHardGate]);
    }

    [Fact]
    public async Task Contribute_NormalEndpoint_ContributionCappedAtGenericMax()
    {
        // Stack two classes at high confidence: per-class weights sum to ~0.25, well
        // under the generic_max_confidence cap of 0.35 (so no clamping observable
        // here), but the resulting Bot contribution should never exceed 0.35.
        var c = BuildContributor(enabled: true,
            new StubProvider("p1", IntelligenceSignalClass.Vulnerability),
            new StubProvider("p2", IntelligenceSignalClass.SuspiciousNetworkRange));
        var state = NewState(path: "/api/products");   // normal

        var contributions = await c.ContributeAsync(state, default);

        Assert.Single(contributions);
        Assert.InRange(contributions[0].ConfidenceDelta, 0.0, 0.35);
    }

    [Fact]
    public async Task Contribute_KevMatchOnSensitivePath_HitsKevFloor()
    {
        // CISA KEV verdict + extracted CVE + sensitive endpoint should pin the
        // contribution at or above kev_match_sensitive_floor (0.85 default).
        var c = BuildContributor(enabled: true, new StubProvider("cisa-kev", IntelligenceSignalClass.Vulnerability));
        var state = NewState(path: "/admin");
        state.SignalWriter![SignalKeys.CveProbeId] = "CVE-2021-44228";

        var contributions = await c.ContributeAsync(state, default);

        Assert.Single(contributions);
        Assert.True(contributions[0].ConfidenceDelta >= 0.85,
            $"expected KEV+sensitive floor >= 0.85, got {contributions[0].ConfidenceDelta}");
        Assert.Equal("CVE-2021-44228", state.Signals[SignalKeys.ThreatIntelKevMatch]);
    }

    // --- helpers ---

    private sealed class StubProvider : IThreatIntelProvider
    {
        private readonly ThreatIntelVerdict _verdict;
        public bool ReturnNull;

        public StubProvider(string name, IntelligenceSignalClass cls)
        {
            Name = name;
            _verdict = new ThreatIntelVerdict
            {
                Provider = name,
                Classification = cls == IntelligenceSignalClass.Vulnerability ? "kev"
                    : cls == IntelligenceSignalClass.TorExit ? "tor"
                    : cls == IntelligenceSignalClass.CloudInfrastructure ? "cloud:aws"
                    : "malicious",
                Confidence = 0.9,
                IntelligenceClass = cls,
                ObservedUtc = DateTime.UtcNow,
                ExpiresUtc = default
            };
        }

        public string Name { get; }
        public ThreatIntelMode Mode => ThreatIntelMode.Offline;
        public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; }
            = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip, ThreatSubjectType.Cve };
        public TimeSpan RefreshInterval => TimeSpan.FromHours(1);
        public ThreatIntelVerdict? TryLookup(ThreatSubject subject) => ReturnNull ? null : _verdict;
        public Task RefreshAsync(ThreatSubject? subject, CancellationToken ct) => Task.CompletedTask;
        public ProviderStatus GetStatus() => new()
        {
            Provider = Name, Mode = Mode, Enabled = true, RefreshInterval = RefreshInterval
        };
    }

    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        public DetectorManifest? GetManifest(string detectorName) => null;
        public DetectorDefaults GetDefaults(string detectorName) => new();
        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;
        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(defaultValue);
        public void InvalidateCache(string? detectorName = null) { }
        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }
}
