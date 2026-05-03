using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class EndpointDivergenceTrackerTests
{
    [Fact]
    public void RecordSession_increments_total_count()
    {
        var tracker = new EndpointDivergenceTracker();
        tracker.RecordSession("/blog/post");
        tracker.RecordSession("/blog/post");
        Assert.Equal(2, tracker.GetStats("/blog/post").TotalSessions);
    }

    [Fact]
    public void RecordDivergence_increments_divergence_count()
    {
        var tracker = new EndpointDivergenceTracker();
        tracker.RecordSession("/blog/post");
        tracker.RecordDivergence("/blog/post");
        Assert.Equal(1, tracker.GetStats("/blog/post").DivergenceCount);
    }

    [Fact]
    public void IsStale_returns_false_below_min_sessions()
    {
        var tracker = new EndpointDivergenceTracker();
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordSession("/page");
            tracker.RecordDivergence("/page");
        }
        Assert.False(tracker.IsStale("/page"));
    }

    [Fact]
    public void IsStale_returns_false_below_rate_threshold()
    {
        var tracker = new EndpointDivergenceTracker();
        for (var i = 0; i < 10; i++)
            tracker.RecordSession("/page");
        for (var i = 0; i < 2; i++)
            tracker.RecordDivergence("/page");
        Assert.False(tracker.IsStale("/page"));
    }

    [Fact]
    public void IsStale_returns_true_above_rate_threshold_with_enough_sessions()
    {
        var tracker = new EndpointDivergenceTracker();
        for (var i = 0; i < 10; i++)
            tracker.RecordSession("/page");
        for (var i = 0; i < 5; i++)
            tracker.RecordDivergence("/page");
        Assert.True(tracker.IsStale("/page"));
    }

    [Fact]
    public void GetStats_unknown_path_returns_zero_stats()
    {
        var tracker = new EndpointDivergenceTracker();
        var stats = tracker.GetStats("/unknown");
        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.DivergenceCount);
    }

    [Fact]
    public void Reset_clears_stats_for_path()
    {
        var tracker = new EndpointDivergenceTracker();
        tracker.RecordSession("/page");
        tracker.RecordDivergence("/page");
        tracker.Reset("/page");
        var stats = tracker.GetStats("/page");
        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.DivergenceCount);
    }
}
