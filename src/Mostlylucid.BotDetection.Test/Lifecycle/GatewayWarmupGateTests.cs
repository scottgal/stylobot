using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Lifecycle;

/// <summary>
///     Pins <see cref="GatewayWarmupGate"/>'s two-dimensional cold-start
///     verdict. Gateway-wide warmup requires both uptime + total-request
///     thresholds; per-signature warmup adds a per-fingerprint observation
///     floor on top so a freshly-observed signature stays gated until it
///     accumulates enough samples to be classified reliably.
/// </summary>
public class GatewayWarmupGateTests
{
    private static GatewayWarmupGate Build(
        DegradationAtom atom,
        GatewayWarmupOptions opts,
        FixedTimeProvider clock)
        => new(atom, Options.Create(opts), clock);

    private static FixedTimeProvider FreshClock()
        => new(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Cold_start_gateway_is_in_warmup_mode()
    {
        // Fresh process, no requests recorded yet. Both gateway-wide
        // dimensions (uptime + sample count) are still cold; the per-
        // signature dimension cannot save us either.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromMinutes(3),
            MinGatewaySamples = 200,
            MinSignatureSamples = 8
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        Assert.False(gate.IsWarmedUp(signatureObservationCount: 100));
        Assert.False(gate.IsWarmedUp());
    }

    [Fact]
    public void After_uptime_AND_min_samples_gateway_is_warmed_up()
    {
        // Both gateway-wide arms satisfied: uptime past WarmupDuration AND
        // total samples past MinGatewaySamples. Per-signature passed too.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromSeconds(1),
            MinGatewaySamples = 200,
            MinSignatureSamples = 8
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        for (var i = 0; i < 250; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(gate.IsWarmedUp(signatureObservationCount: 100));
        Assert.True(gate.IsWarmedUp());
    }

    [Fact]
    public void Uptime_past_threshold_but_too_few_samples_stays_in_warmup()
    {
        // The "boot 10 minutes ago, served 3 requests" case. Uptime is not
        // enough on its own; behavioural arms must wait for the sample
        // floor.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromSeconds(1),
            MinGatewaySamples = 200,
            MinSignatureSamples = 8
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 3; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");

        Assert.False(gate.IsWarmedUp(signatureObservationCount: 100));
    }

    [Fact]
    public void Many_samples_but_below_uptime_stays_in_warmup()
    {
        // The "burst of synthetic traffic at boot" case. A flood of
        // requests in the first second shouldn't trick the gate into
        // releasing behavioural arms before the uptime floor.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromMinutes(3),
            MinGatewaySamples = 200,
            MinSignatureSamples = 8
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        for (var i = 0; i < 500; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        // Uptime advances zero -- still inside warmup window.

        Assert.False(gate.IsWarmedUp(signatureObservationCount: 100));
    }

    [Fact]
    public void Per_signature_warmup_overrides_gateway_warmup_when_signature_is_new()
    {
        // Gateway is fully warm (lots of uptime + samples) but the
        // specific signature has only 3 observations. Behavioural
        // contribution for THAT signature must stand down.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromSeconds(1),
            MinGatewaySamples = 200,
            MinSignatureSamples = 8
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        for (var i = 0; i < 500; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(gate.IsWarmedUp(signatureObservationCount: 3));
        // Crossing the floor flips the verdict for this signature.
        Assert.True(gate.IsWarmedUp(signatureObservationCount: 10));
    }

    [Fact]
    public void Disabled_via_option_always_returns_warmed_up()
    {
        // The master switch must short-circuit cleanly so test environments
        // and controlled benchmarks can run detectors from the first
        // request without statistical-floor interference.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions { EnableWarmupGate = false };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        Assert.True(gate.IsWarmedUp(signatureObservationCount: 0));
        Assert.True(gate.IsWarmedUp());
    }

    [Fact]
    public void Signature_observation_count_zero_is_always_in_warmup()
    {
        // Defensive: a brand-new signature with zero observations is by
        // definition under the floor. Gate must say cold regardless of
        // gateway state.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromSeconds(1),
            MinGatewaySamples = 1,
            MinSignatureSamples = 1
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        atom.RecordResponse(200, latencyMs: 5, path: "/");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(gate.IsWarmedUp(signatureObservationCount: 0));
        Assert.True(gate.IsWarmedUp(signatureObservationCount: 1));
    }

    [Fact]
    public void IsWarmedUp_overload_without_signature_skips_per_signature_dimension()
    {
        // The parameterless overload is what BlackboardOrchestrator calls
        // at entry (no per-signature context yet) -- it must check only the
        // gateway-wide dimensions, never gating on the per-signature floor.
        var atom = new DegradationAtom();
        var opts = new GatewayWarmupOptions
        {
            WarmupDuration = TimeSpan.FromSeconds(1),
            MinGatewaySamples = 5,
            MinSignatureSamples = 10000 // would never be satisfied if checked
        };
        var clock = FreshClock();
        var gate = Build(atom, opts, clock);

        for (var i = 0; i < 10; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(gate.IsWarmedUp());
    }
}