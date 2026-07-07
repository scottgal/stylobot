using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.HealthEndpoints;

/// <summary>
///     Acceptance tests for Task 7, Step 1: probe-UA family coverage.
///
///     Verifies that EVERY <see cref="HealthEndpointOptions.DefaultProbeUserAgents"/>
///     family (kube-probe, Go-http-client, curl, wget, docker) is classified as
///     <see cref="BotType.Internal"/> with name "Health Probe" when the request
///     arrives from a loopback IP and the <c>health.endpoint</c> signal is raised
///     via the production sink path.
///
///     The existing <c>DetectionLedgerReaderFixTests.HealthProbe_SinkPath_ProbeUA_ClassifiesAsInternal</c>
///     pins the sink-path wiring for curl only. This theory extends coverage to all
///     five default families so a regression in any one of them is caught without
///     duplicating the sink-path plumbing test.
/// </summary>
public sealed class ProbeUaFamilyCoverageTests
{
    /// <summary>
    ///     Each of the five default probe UA families must classify as
    ///     <see cref="BotType.Internal"/> with <see cref="FingerprintNameComposer.HealthProbeName"/>
    ///     when the request is loopback-sourced and the health.endpoint signal is
    ///     raised via <c>sink.Raise</c> (the production path, not premergedSignals).
    /// </summary>
    [Theory]
    [InlineData("kube-probe/1.28")]
    [InlineData("kube-probe/1.30")]
    [InlineData("Go-http-client/2.0")]
    [InlineData("Go-http-client/1.1")]
    [InlineData("curl/8.5.0")]
    [InlineData("Wget/1.21.4")]
    [InlineData("wget/1.20.3")]         // lowercase variant
    [InlineData("Docker/1.0 check")]
    [InlineData("docker/20.10")]        // lowercase variant
    public void LoopbackHealthRequest_ProbeUA_ClassifiesAsInternalWithHealthProbeName(string ua)
    {
        // Arrange: production sink path (all signals raised via sink.Raise, no premergedSignals).
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "ip");
        sink.Raise($"{SignalKeys.HealthEndpoint}:true", "health");
        // SignalKeys.UserAgent is the key read by ProbeShapeClassifier.IsProbeShape via sink.ReadHint.
        sink.Raise($"{SignalKeys.UserAgent}:{ua}", "ua");

        var ledger = new DetectionLedger($"probe-ua-coverage-{ua}");
        // A non-zero confidence contribution is required so the orchestrator produces
        // a non-default AggregatedEvidence; the UA bot-type annotation mimics what
        // UserAgentContributor emits for tool-like UAs.
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.85,
            Weight = 1.0,
            BotType = BotType.Tool.ToString(),
            Reason = $"{ua} - tool UA",
        });

        // Act: no premergedSignals -> exercises the production sink path.
        var evidence = ledger.ToAggregatedEvidence(
            options: new BotDetectionOptions(),
            sink: sink);

        // Assert: loopback + health_endpoint + probe UA -> Internal, regardless of UA-derived type.
        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(FingerprintNameComposer.HealthProbeName, evidence.PrimaryBotName);
    }

    /// <summary>
    ///     Shape guard: a browser-navigated request to a health endpoint from a
    ///     loopback IP must NOT receive <see cref="BotType.Internal"/> or the
    ///     "Health Probe" name. Source alone (local IP) is insufficient -- the
    ///     probe shape must be confirmed by a probe-family UA without Sec-Fetch-Mode:navigate.
    ///
    ///     This is the Task 7 Step 2 acceptance assertion: "browser-shaped /health
    ///     from trusted IP is NOT auto-allowed".
    /// </summary>
    [Fact]
    public void LoopbackBrowserNavigation_OnHealthEndpoint_NotInternalAndNotHealthProbeName()
    {
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "ip");
        sink.Raise($"{SignalKeys.HealthEndpoint}:true", "health");
        sink.Raise($"{SignalKeys.UserAgent}:Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/131", "ua");
        // Sec-Fetch-Mode: navigate is the shape-guard trigger.
        sink.Raise($"{SignalKeys.HeaderSecFetchMode}:navigate", "sfm");

        var ledger = new DetectionLedger("probe-ua-coverage-browser-shape-guard");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.1,
            Weight = 1.0,
            Reason = "Chrome browser UA",
        });

        var evidence = ledger.ToAggregatedEvidence(
            options: new BotDetectionOptions(),
            sink: sink);

        Assert.NotEqual(BotType.Internal, evidence.PrimaryBotType);
        Assert.NotEqual(FingerprintNameComposer.HealthProbeName, evidence.PrimaryBotName);
    }
}
