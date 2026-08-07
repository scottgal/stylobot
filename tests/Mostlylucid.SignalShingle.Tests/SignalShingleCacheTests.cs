using Microsoft.Extensions.Time.Testing;
using Mostlylucid.SignalShingle;

namespace Mostlylucid.SignalShingle.Tests;

public sealed class SignalShingleCacheTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Read_is_warming_until_the_materializer_publishes()
    {
        var cache = Create();
        var read = cache.Read("traffic", SignalShingleDemand.Create("traffic-widget", TimeSpan.FromMinutes(1)));

        Assert.False(read.IsWarm);
        var candidate = Assert.Single(cache.AcquireRefreshCandidates(1));
        Assert.Equal("traffic", candidate.Key);
        Assert.True(cache.CompleteRefresh(candidate, "ready", generation: 7));

        var warm = cache.Read("traffic");
        Assert.True(warm.IsWarm);
        Assert.Equal("ready", warm.Value);
        Assert.Equal(7, warm.Generation);
    }

    [Fact]
    public void fastest_live_lease_controls_shared_refresh_cadence_and_expiry_relaxes_it()
    {
        var cache = Create();
        cache.Publish("traffic", "ready", 1);
        cache.Read("traffic", SignalShingleDemand.Create("slow", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)));
        cache.Read("traffic", SignalShingleDemand.Create("fast", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1.5)));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.Single(cache.AcquireRefreshCandidates(1)).EffectiveRefreshInterval);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(5), cache.Read("traffic").EffectiveRefreshInterval);
    }

    [Fact]
    public void older_generation_cannot_overwrite_newer_projection()
    {
        var cache = Create();
        Assert.True(cache.Publish("traffic", "new", 9));
        Assert.False(cache.Publish("traffic", "old", 8));
        Assert.Equal("new", cache.Read("traffic").Value);
    }

    [Fact]
    public void over_stale_value_returns_warming_instead_of_silently_lying()
    {
        var cache = Create(maximumStaleness: TimeSpan.FromMinutes(2));
        cache.Publish("traffic", "old", 1);
        _time.Advance(TimeSpan.FromMinutes(3));

        var read = cache.Read("traffic");
        Assert.Equal(SignalShingleState.Warming, read.State);
        Assert.Null(read.Value);
    }

    [Fact]
    public void dirty_signal_received_during_refresh_survives_completion()
    {
        var cache = Create();
        cache.Pin("traffic");
        var candidate = Assert.Single(cache.AcquireRefreshCandidates(1));
        Assert.True(cache.MarkDirty("traffic"));

        Assert.True(cache.CompleteRefresh(candidate, "old-result", 1));
        var retry = Assert.Single(cache.AcquireRefreshCandidates(1));
        Assert.True(retry.IsDirty);
    }

    [Fact]
    public void refresh_lease_prevents_duplicate_concurrent_work()
    {
        var cache = Create();
        cache.Pin("traffic");
        Assert.Single(cache.AcquireRefreshCandidates(1));
        Assert.Empty(cache.AcquireRefreshCandidates(1));
    }

    private SignalShingleCache<string, string> Create(TimeSpan? maximumStaleness = null) => new(
        new SignalShingleCacheOptions { Capacity = 8, DefaultRefreshInterval = TimeSpan.FromMinutes(5), MaximumStaleness = maximumStaleness ?? TimeSpan.FromMinutes(30) }, _time);
}
