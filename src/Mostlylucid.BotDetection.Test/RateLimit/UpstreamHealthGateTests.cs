using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins <see cref="UpstreamHealthGate"/>'s read-only verdict over
///     <see cref="DegradationAtom"/>'s rolling EMAs. The gate is the
///     boundary status-derived detector contributions check before
///     elevating bot score so a cold-starting / down upstream doesn't
///     get falsely attributed to bot probing.
/// </summary>
public class UpstreamHealthGateTests
{
    [Fact]
    public void Cold_start_zero_responses_is_healthy()
    {
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));
        Assert.True(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void Below_min_sample_count_is_healthy_even_when_burst_is_all_500s()
    {
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions { MinSampleCount = 10 };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        for (var i = 0; i < 5; i++)
            atom.RecordResponse(500, latencyMs: 5, path: "/");

        Assert.True(gate.IsUpstreamHealthy(),
            "Five 500s do not constitute enough evidence to flip; cold start protection.");
    }

    [Fact]
    public void Over_threshold_5xx_after_min_samples_is_unhealthy()
    {
        // Outage shape: a sustained burst of 5xx that hasn't recovered yet.
        // The EWMA reads ~1.0 after a steady stream of 5xx, well above
        // the 0.25 threshold.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        for (var i = 0; i < 50; i++)
            atom.RecordResponse(503, latencyMs: 5, path: "/");

        Assert.False(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void Mixed_traffic_with_majority_5xx_is_unhealthy()
    {
        // Alternating 503 / 200 sustains the 5xx EWMA around 0.5 -- well
        // above the 0.25 threshold and verifies the gate reacts to mixed
        // (not just contiguous) outage signal.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        for (var i = 0; i < 60; i++)
            atom.RecordResponse(i % 2 == 0 ? 503 : 200, latencyMs: 5, path: "/");

        Assert.False(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void Over_threshold_4xx_after_min_samples_is_unhealthy()
    {
        // Outage shape: every route returns 4xx because routing is broken /
        // origin migration left the gateway pointing at a wrong upstream.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        for (var i = 0; i < 50; i++)
            atom.RecordResponse(404, latencyMs: 5, path: "/missing");

        Assert.False(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void After_recovery_flips_back_to_healthy()
    {
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        // Outage shape: upstream is returning all 5xx; gate flips unhealthy.
        for (var i = 0; i < 50; i++)
            atom.RecordResponse(503, latencyMs: 5, path: "/");
        Assert.False(gate.IsUpstreamHealthy());

        // Origin recovers: long stream of healthy responses decays the EWMA
        // toward zero and the gate flips back to healthy.
        for (var i = 0; i < 200; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        Assert.True(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void Healthy_traffic_with_one_off_404_stays_healthy()
    {
        // The whole point: legitimate user stale-bookmark traffic must NOT
        // flip the gate or the gate would be useless.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        for (var i = 0; i < 199; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        atom.RecordResponse(404, latencyMs: 5, path: "/missing");

        Assert.True(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void Pure_429s_do_NOT_flip_gate_to_unhealthy()
    {
        // 429s mean rate-limit-enforcement upstream is working, not that
        // upstream is down. They MUST remain meaningful bot signals.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };
        var gate = new UpstreamHealthGate(atom, Options.Create(opts));

        for (var i = 0; i < 100; i++)
            atom.RecordResponse(429, latencyMs: 5, path: "/api");

        Assert.True(gate.IsUpstreamHealthy());
    }
}