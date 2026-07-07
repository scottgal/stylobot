using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Pins the "Health Probe" display-name assignment for Task 6.
///
///     The name must be set at the classification site in
///     <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/> where
///     <c>isHealthProbe</c> is already computed via the sink-first bool reader.
///     The <see cref="FingerprintNameComposer.Compose"/> path is intentionally
///     NOT the fix site: <c>health_endpoint</c> is a sink-only signal that is
///     never present in the <c>preSignals</c> dict the composer receives, so a
///     short-circuit inside the composer would silently never fire (same class
///     of bug as Task 1).
///
///     Test 1: production sink path -- loopback + curl-UA + health_endpoint
///     raised via sink.Raise yields PrimaryBotName == "Health Probe".
///
///     Test 2: shape-guard negative -- loopback + browser-navigation shape +
///     health-endpoint path does NOT get "Health Probe" (shape guard rejects it).
/// </summary>
public sealed class FingerprintNameComposerHealthProbeTests
{
    /// <summary>
    ///     Production sink path. IpIsLocal + HealthEndpoint + probe UA raised via
    ///     sink.Raise (not premergedSignals) must yield PrimaryBotName ==
    ///     <see cref="FingerprintNameComposer.HealthProbeName"/>.
    ///     Proves the name is set on the real sink code-path, not only when signals
    ///     are hand-built in a premergedSignals dict.
    /// </summary>
    [Fact]
    public void HealthProbe_SinkPath_ProbeUA_NamedHealthProbe()
    {
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "ip");
        sink.Raise($"{SignalKeys.HealthEndpoint}:true", "health");
        // ua.raw is the key read by ProbeShapeClassifier.IsProbeShape via sink.ReadHint.
        sink.Raise($"{SignalKeys.UserAgent}:curl/8.5.0", "ua");

        var ledger = new DetectionLedger("test-health-probe-name-sink");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.85,
            Weight = 1.0,
            BotType = BotType.Tool.ToString(),
            Reason = "curl/8.5.0 - tool UA",
        });

        // No premergedSignals: exercises the production sink path where atoms
        // raise signals via sink.Raise and never populate MergedSignals.
        var evidence = ledger.ToAggregatedEvidence(
            options: new BotDetectionOptions(),
            sink: sink);

        // BotType must also be Internal (Task 3 regression guard).
        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        // Task 6: the display name must be the canonical constant, not a UA-derived
        // string like "curl/8.5.0" or "Unknown".
        Assert.Equal(FingerprintNameComposer.HealthProbeName, evidence.PrimaryBotName);
    }

    /// <summary>
    ///     Early-exit path. When FastPathReputation fires TriggerEarlyExit=true and
    ///     the sink carries IpIsLocal + HealthEndpoint + probe UA, the early-exit
    ///     parity arm in CreateEarlyExitResult must set PrimaryBotName ==
    ///     <see cref="FingerprintNameComposer.HealthProbeName"/>.
    ///
    ///     Exercises the early-exit code path that ToAggregatedEvidence's
    ///     main-path assignment cannot reach (the main assignment is dead once
    ///     ledger.EarlyExit is true).
    /// </summary>
    [Fact]
    public void HealthProbe_EarlyExitPath_ProbeUA_NamedHealthProbe()
    {
        var ledger = new DetectionLedger("test-health-probe-early-exit");
        // Mimics a FastPathReputation early-exit (the most common early-exit trigger).
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "FastPathReputation",
            Category = "Reputation",
            ConfidenceDelta = 1.0,
            Weight = 3.0,
            Reason = "IP seen 50 times",
            TriggerEarlyExit = true,
            EarlyExitVerdict = nameof(EarlyExitVerdict.VerifiedBadBot)
        });

        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "ip");
        sink.Raise($"{SignalKeys.HealthEndpoint}:true", "health");
        sink.Raise($"{SignalKeys.UserAgent}:curl/8.5.0", "ua");

        var evidence = ledger.ToAggregatedEvidence(
            options: new BotDetectionOptions(),
            sink: sink);

        Assert.True(evidence.EarlyExit, "Early-exit path must be taken");
        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(FingerprintNameComposer.HealthProbeName, evidence.PrimaryBotName);
    }

    /// <summary>
    ///     Early-exit negative case. Local IP with no HealthEndpoint signal
    ///     (non-health LAN traffic, e.g. a curl script that hits an API path)
    ///     must NOT receive the "Health Probe" name even on the early-exit path.
    /// </summary>
    [Fact]
    public void HealthProbe_EarlyExitPath_LocalIpNoHealthEndpoint_NotNamedHealthProbe()
    {
        var ledger = new DetectionLedger("test-health-probe-early-exit-negative");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "FastPathReputation",
            Category = "Reputation",
            ConfidenceDelta = 1.0,
            Weight = 3.0,
            Reason = "IP seen 50 times",
            TriggerEarlyExit = true,
            EarlyExitVerdict = nameof(EarlyExitVerdict.VerifiedBadBot)
        });

        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "ip");
        // No HealthEndpoint signal raised.

        var evidence = ledger.ToAggregatedEvidence(
            options: new BotDetectionOptions(),
            sink: sink);

        Assert.True(evidence.EarlyExit, "Early-exit path must be taken");
        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.NotEqual(FingerprintNameComposer.HealthProbeName, evidence.PrimaryBotName);
    }

    /// <summary>
    ///     Shape-guard negative case. Loopback + browser-navigation shape + health
    ///     endpoint must NOT yield PrimaryBotName == "Health Probe": the shape guard
    ///     (Sec-Fetch-Mode: navigate) rejects the probe classification, so the
    ///     browser request is not a health probe and must not receive the probe name.
    /// </summary>
    [Fact]
    public void HealthProbe_ShapeGuard_BrowserNavigation_NotNamedHealthProbe()
    {
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "ip");
        sink.Raise($"{SignalKeys.HealthEndpoint}:true", "health");
        sink.Raise($"{SignalKeys.UserAgent}:Mozilla/5.0 (Windows NT 10.0) Chrome/120", "ua");
        sink.Raise($"{SignalKeys.HeaderSecFetchMode}:navigate", "sfm");

        var ledger = new DetectionLedger("test-health-probe-name-shape-guard");
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

        // Shape guard must prevent the probe classification.
        Assert.NotEqual(BotType.Internal, evidence.PrimaryBotType);
        Assert.NotEqual(FingerprintNameComposer.HealthProbeName, evidence.PrimaryBotName);
    }
}
