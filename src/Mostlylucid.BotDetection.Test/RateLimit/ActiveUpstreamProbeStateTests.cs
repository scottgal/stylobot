using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the <see cref="ActiveUpstreamProbeState"/> worst-case fold semantics
///     and the <see cref="UpstreamHealthGate"/> composite that uses it.
/// </summary>
public class ActiveUpstreamProbeStateTests
{
    // ---------------------------------------------------------------------------
    // ActiveUpstreamProbeState
    // ---------------------------------------------------------------------------

    [Fact]
    public void Latest_returns_null_before_any_update()
    {
        var state = new ActiveUpstreamProbeState();
        Assert.Null(state.Latest("upstream-a"));
    }

    [Fact]
    public void Latest_returns_the_last_snapshot_after_update()
    {
        var state = new ActiveUpstreamProbeState();
        var snapshot = new ActiveProbeSnapshot("healthy", 42, DateTimeOffset.UtcNow, null);
        state.Update("upstream-a", snapshot);
        Assert.Equal(snapshot, state.Latest("upstream-a"));
    }

    [Fact]
    public void AggregateHealthy_returns_null_when_empty()
    {
        var state = new ActiveUpstreamProbeState();
        Assert.Null(state.AggregateHealthy());
    }

    [Fact]
    public void AggregateHealthy_returns_true_when_sole_upstream_is_healthy()
    {
        var state = new ActiveUpstreamProbeState();
        state.Update("upstream-a", new ActiveProbeSnapshot("healthy", 10, DateTimeOffset.UtcNow, null));
        Assert.True(state.AggregateHealthy());
    }

    [Fact]
    public void AggregateHealthy_returns_false_when_any_upstream_is_unhealthy()
    {
        var state = new ActiveUpstreamProbeState();
        state.Update("upstream-a", new ActiveProbeSnapshot("healthy", 10, DateTimeOffset.UtcNow, null));
        state.Update("upstream-b", new ActiveProbeSnapshot("unhealthy", 999, DateTimeOffset.UtcNow, "TCP timeout"));
        Assert.False(state.AggregateHealthy());
    }

    [Fact]
    public void AggregateHealthy_treats_unknown_as_not_unhealthy()
    {
        // "unknown" snapshots count as known-but-not-unhealthy; they do NOT
        // force the fold to false -- only an explicit "unhealthy" does.
        var state = new ActiveUpstreamProbeState();
        state.Update("upstream-a", new ActiveProbeSnapshot("unknown", 0, DateTimeOffset.UtcNow, "probe pending"));
        Assert.True(state.AggregateHealthy());
    }

    // ---------------------------------------------------------------------------
    // UpstreamHealthGate composite
    // ---------------------------------------------------------------------------

    private static UpstreamHealthGate MakeGate(
        DegradationAtom atom,
        UpstreamHealthOptions opts,
        IActiveUpstreamProbeState? probeState = null)
        => new(atom, Options.Create(opts), probeState);

    [Fact]
    public void Composite_passive_confident_5xx_over_threshold_unhealthy_regardless_of_healthy_active()
    {
        // Passive data is authoritative when it has enough samples.
        // Even an injected "healthy" active state must not override a
        // confirmed 5xx outage verdict.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions
        {
            Unhealthy5xxThreshold = 0.25,
            Unhealthy4xxThreshold = 0.5,
            MinSampleCount = 10
        };

        for (var i = 0; i < 50; i++)
            atom.RecordResponse(503, latencyMs: 5, path: "/");

        var probeState = new ActiveUpstreamProbeState();
        probeState.Update("upstream-a", new ActiveProbeSnapshot("healthy", 5, DateTimeOffset.UtcNow, null));

        var gate = MakeGate(atom, opts, probeState);
        Assert.False(gate.IsUpstreamHealthy(),
            "Passive dominates when confident: 5xx outage should report unhealthy even with a healthy active snapshot.");
    }

    [Fact]
    public void Composite_passive_data_starved_active_unhealthy_returns_false()
    {
        // Cold-start / idle: passive has no samples, so active fills the gap.
        // An active "unhealthy" upstream should propagate to the gate.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions { MinSampleCount = 10 };

        var probeState = new ActiveUpstreamProbeState();
        probeState.Update("upstream-a", new ActiveProbeSnapshot("unhealthy", 999, DateTimeOffset.UtcNow, "TCP timeout"));

        var gate = MakeGate(atom, opts, probeState);
        Assert.False(gate.IsUpstreamHealthy(),
            "Passive data-starved + active unhealthy: gate should return false.");
    }

    [Fact]
    public void Composite_passive_data_starved_active_healthy_returns_true()
    {
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions { MinSampleCount = 10 };

        var probeState = new ActiveUpstreamProbeState();
        probeState.Update("upstream-a", new ActiveProbeSnapshot("healthy", 8, DateTimeOffset.UtcNow, null));

        var gate = MakeGate(atom, opts, probeState);
        Assert.True(gate.IsUpstreamHealthy());
    }

    [Fact]
    public void Composite_passive_data_starved_no_active_state_returns_true()
    {
        // null probeState = cold-start unchanged: gate stays open.
        var atom = new DegradationAtom();
        var opts = new UpstreamHealthOptions { MinSampleCount = 10 };

        var gate = MakeGate(atom, opts, probeState: null);
        Assert.True(gate.IsUpstreamHealthy(),
            "Cold-start with no active state must preserve the existing unconditional-true behaviour.");
    }
}
