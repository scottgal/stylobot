using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the snapshot-on-Tick10s behaviour of
///     <see cref="DegradationHistorySampler"/>: each tick reads the
///     <see cref="DegradationAtom"/>'s current EWMA arms and appends one
///     <see cref="DegradationSnapshot"/> to the bounded ring.
/// </summary>
public class DegradationHistorySamplerTests
{
    [Fact]
    public async Task OnTickAsync_writes_a_snapshot_whose_arms_match_the_atom()
    {
        var atom = new DegradationAtom();
        // Drive the atom into a non-trivial state -- the gate's "outage shape"
        // is what the dashboard is meant to surface.
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(500, latencyMs: 25, path: "/");

        var history = new DegradationHistoryAtom(Options.Create(new SiteHealthHistoryOptions { Capacity = 10 }));
        var sampler = new DegradationHistorySampler(atom, history, NullLogger<DegradationHistorySampler>.Instance);

        await sampler.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var rows = history.GetWindow(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        Assert.Single(rows);
        var snap = rows[0];
        Assert.True(snap.Latency5xxRate > 0.9, $"5xx EWMA should reflect the atom; got {snap.Latency5xxRate}");
        Assert.True(snap.LatencyP95Ms > 0, $"latency EWMA should be populated; got {snap.LatencyP95Ms}");
    }

    [Fact]
    public async Task Multiple_ticks_append_multiple_snapshots()
    {
        var atom = new DegradationAtom();
        var history = new DegradationHistoryAtom(Options.Create(new SiteHealthHistoryOptions { Capacity = 10 }));
        var sampler = new DegradationHistorySampler(atom, history, NullLogger<DegradationHistorySampler>.Instance);

        var t0 = DateTimeOffset.UtcNow;
        await sampler.OnTickAsync(t0, CancellationToken.None);
        await sampler.OnTickAsync(t0.AddSeconds(10), CancellationToken.None);
        await sampler.OnTickAsync(t0.AddSeconds(20), CancellationToken.None);

        var rows = history.GetWindow(t0.AddMinutes(1).UtcDateTime, TimeSpan.FromMinutes(2));
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Disposed_sampler_skips_subsequent_ticks()
    {
        var atom = new DegradationAtom();
        var history = new DegradationHistoryAtom(Options.Create(new SiteHealthHistoryOptions { Capacity = 10 }));
        var sampler = new DegradationHistorySampler(atom, history, NullLogger<DegradationHistorySampler>.Instance);

        await sampler.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        sampler.Dispose();
        await sampler.OnTickAsync(DateTimeOffset.UtcNow.AddSeconds(10), CancellationToken.None);

        var rows = history.GetWindow(DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(2));
        Assert.Single(rows);
    }
}