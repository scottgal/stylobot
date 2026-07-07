using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.HealthEndpoints;

/// <summary>
///     Tests for <see cref="HealthEndpointReconAtom"/>: external (non-probe) hits on
///     health endpoints raise <see cref="SignalKeys.HealthEndpointRecon"/> and nudge
///     <see cref="SignalKeys.IntentThreatScore"/>, while legitimate local probes do not.
/// </summary>
public sealed class HealthReconThreatScoreTests
{
    private static readonly IReadOnlyList<string> DefaultProbeUas =
        HealthEndpointOptions.DefaultProbeUserAgents;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HealthEndpointReconAtom BuildAtom(HttpContext context)
    {
        var catalog = new HealthEndpointCatalog(Options.Create(HealthEndpointOptions.Default));
        var options = Options.Create(HealthEndpointOptions.Default);
        return new HealthEndpointReconAtom(
            NullLogger<HealthEndpointReconAtom>.Instance,
            catalog,
            options,
            new StaticHttpContextAccessor(context));
    }

    private static SignalSink PreSeededSink(bool healthEndpoint, bool ipIsLocal, string? ua = null, string? secFetchMode = null)
    {
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        if (healthEndpoint)
            sink.Raise($"{SignalKeys.HealthEndpoint}:true", "session");
        if (ipIsLocal)
            sink.Raise($"{SignalKeys.IpIsLocal}:true", "session");
        else
            sink.Raise($"{SignalKeys.IpIsLocal}:false", "session");
        if (ua is not null)
            sink.Raise($"{SignalKeys.UserAgent}:{ua}", "session");
        if (secFetchMode is not null)
            sink.Raise($"{SignalKeys.HeaderSecFetchMode}:{secFetchMode}", "session");
        return sink;
    }

    private static IReadOnlyDictionary<string, string> SnapshotSink(SignalSink sink)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in sink.Sense(_ => true))
        {
            var raw = e.Signal;
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            dict.TryAdd(raw[..colon], raw[(colon + 1)..]);
        }
        return dict;
    }

    // ── Task 4: atom raises health.endpoint_recon on external hit ────────────

    /// <summary>
    ///     External source (ip.is_local=false), browser UA, health path ->
    ///     HealthEndpointReconAtom must raise <c>health.endpoint_recon:true</c> AND
    ///     a non-zero <c>intent.threat_score</c> into the sink.
    /// </summary>
    [Fact]
    public async Task ExternalBrowserHit_RaisesReconAndThreatNudge()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        var sink = PreSeededSink(healthEndpoint: true, ipIsLocal: false,
            ua: "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/131",
            secFetchMode: "navigate");

        var atom = BuildAtom(context);
        await atom.DetectAsync(sink, "session");

        var signals = SnapshotSink(sink);

        signals.Should().ContainKey(SignalKeys.HealthEndpointRecon,
            "external browser hit on a health endpoint is reconnaissance");
        signals[SignalKeys.HealthEndpointRecon].Should().Be("true");

        signals.Should().ContainKey(SignalKeys.IntentThreatScore,
            "recon on a health endpoint must nudge the threat score");
        double.TryParse(signals[SignalKeys.IntentThreatScore], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var score).Should().BeTrue();
        score.Should().BeGreaterThan(0.0, "nudge must be positive");
    }

    /// <summary>
    ///     External source with a non-probe UA (no probe-family token) hitting a
    ///     health path must also raise recon even when ip.is_local is false.
    /// </summary>
    [Fact]
    public async Task ExternalNonProbeUA_RaisesRecon()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/healthz";

        // python-requests is NOT in the default probe UA list.
        var sink = PreSeededSink(healthEndpoint: true, ipIsLocal: false,
            ua: "python-requests/2.28.0");

        var atom = BuildAtom(context);
        await atom.DetectAsync(sink, "session");

        var signals = SnapshotSink(sink);
        signals.Should().ContainKey(SignalKeys.HealthEndpointRecon,
            "external non-probe UA hitting a health path is reconnaissance");
    }

    // ── Task 4: legitimate local probe does NOT raise recon ──────────────────

    /// <summary>
    ///     Local IP (ip.is_local=true) with a probe-shaped UA -> NOT reconnaissance.
    ///     This is the Task-3 Internal case; Task 4 must not flag it.
    /// </summary>
    [Fact]
    public async Task LocalProbeUA_DoesNotRaiseRecon()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        var sink = PreSeededSink(healthEndpoint: true, ipIsLocal: true,
            ua: "kube-probe/1.28");

        var atom = BuildAtom(context);
        await atom.DetectAsync(sink, "session");

        var signals = SnapshotSink(sink);
        signals.Should().NotContainKey(SignalKeys.HealthEndpointRecon,
            "a legitimate local probe must not be flagged as reconnaissance");
    }

    /// <summary>
    ///     Local IP with curl UA (probe shape, no Sec-Fetch-Mode:navigate) -> NOT recon.
    /// </summary>
    [Fact]
    public async Task LocalCurlProbe_DoesNotRaiseRecon()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/readyz";

        var sink = PreSeededSink(healthEndpoint: true, ipIsLocal: true,
            ua: "curl/8.5.0");

        var atom = BuildAtom(context);
        await atom.DetectAsync(sink, "session");

        var signals = SnapshotSink(sink);
        signals.Should().NotContainKey(SignalKeys.HealthEndpointRecon,
            "curl from loopback is a legitimate probe, not reconnaissance");
    }

    /// <summary>
    ///     Local IP with browser navigation shape (Sec-Fetch-Mode:navigate) ->
    ///     IS reconnaissance: source alone does not grant the probe pass when shape
    ///     is browser-shaped (Task 3's shape guard applies here too).
    /// </summary>
    [Fact]
    public async Task LocalBrowserNavigationShape_RaisesRecon()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";

        var sink = PreSeededSink(healthEndpoint: true, ipIsLocal: true,
            ua: "Mozilla/5.0 (Windows NT 10.0) Chrome/131",
            secFetchMode: "navigate");

        var atom = BuildAtom(context);
        await atom.DetectAsync(sink, "session");

        var signals = SnapshotSink(sink);
        signals.Should().ContainKey(SignalKeys.HealthEndpointRecon,
            "a browser-navigated health probe from loopback is anomalous; shape guard must fire");
    }

    // ── Task 4: atom is a no-op when health_endpoint signal is absent ────────

    [Fact]
    public async Task NoHealthEndpointSignal_DoesNothing()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/products";

        // health_endpoint NOT set -- atom must skip silently.
        var sink = PreSeededSink(healthEndpoint: false, ipIsLocal: false,
            ua: "curl/8.5.0");

        var atom = BuildAtom(context);
        var contributions = await atom.DetectAsync(sink, "session");

        contributions.Should().BeEmpty("atom must return None() when health_endpoint is absent");
        var signals = SnapshotSink(sink);
        signals.Should().NotContainKey(SignalKeys.HealthEndpointRecon);
    }

    // ── Task 4: threat-score nudge composes with other recon signals ─────────

    /// <summary>
    ///     Proves the nudge COMPOSES: co-occurring with a lower attack signal, the
    ///     health-recon nudge (intent.threat_score) elevates the final threat score
    ///     beyond what the other signal alone would produce.
    ///
    ///     <para>
    ///         Uses <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/> with
    ///         <c>premergedSignals</c> to exercise the production threat-score
    ///         extraction path (<see cref="ExtractThreatScore"/> takes Math.Max across
    ///         all contributors). Scenario: attack.severity:low contributes 0.30 alone;
    ///         adding health-recon's intent.threat_score:0.35 raises the final score
    ///         to 0.35, which is higher than 0.30.
    ///     </para>
    /// </summary>
    [Fact]
    public void ThreatNudgeComposesWithOtherReconSignal()
    {
        // Nudge value the atom will raise -- must match HealthEndpointReconAtom.ReconNudge.
        const double healthReconNudge = HealthEndpointReconAtom.ReconNudge;

        // Scenario A: another recon signal (attack.severity:low -> 0.30) alone.
        var signalsA = new Dictionary<string, object>
        {
            [SignalKeys.AttackSeverity] = "low"    // 0.30 via ExtractThreatScore
        };

        // Scenario C: both the other recon signal AND the health-recon nudge.
        var signalsC = new Dictionary<string, object>
        {
            [SignalKeys.AttackSeverity]   = "low",
            [SignalKeys.IntentThreatScore] = healthReconNudge
        };

        var ledger = new DetectionLedger("threat-compose-test");
        var evidenceA = ledger.ToAggregatedEvidence(
            premergedSignals: signalsA,
            options: new BotDetectionOptions());
        var evidenceC = ledger.ToAggregatedEvidence(
            premergedSignals: signalsC,
            options: new BotDetectionOptions());

        evidenceA.ThreatScore.Should().BeLessThan(healthReconNudge,
            "low attack severity alone ({0:F2}) must be below the health-recon nudge ({1:F2})",
            evidenceA.ThreatScore, healthReconNudge);

        evidenceC.ThreatScore.Should().BeGreaterThan(evidenceA.ThreatScore,
            "adding health-recon nudge ({0:F2}) on top of other recon signal ({1:F2}) " +
            "must raise the combined threat score",
            evidenceC.ThreatScore, evidenceA.ThreatScore);

        evidenceC.ThreatScore.Should().BeApproximately(healthReconNudge, 0.001,
            "combined threat score must equal the nudge (which dominates the low-severity signal)");
    }
}
