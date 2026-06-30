using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the ring-buffer semantics + window-filter behaviour of
///     <see cref="DegradationHistoryAtom"/>. Covers append correctness,
///     overflow rollover, time-window filtering, empty-ring read.
/// </summary>
public class DegradationHistoryAtomTests
{
    [Fact]
    public void GetWindow_returns_empty_when_no_samples_appended()
    {
        var atom = NewAtom(capacity: 10);
        var rows = atom.GetWindow(DateTime.UtcNow, TimeSpan.FromHours(1));
        Assert.Empty(rows);
    }

    [Fact]
    public void Append_then_GetWindow_returns_inserted_samples_oldest_first()
    {
        var atom = NewAtom(capacity: 10);
        var t0 = DateTime.UtcNow;
        atom.Append(Snapshot(t0));
        atom.Append(Snapshot(t0.AddSeconds(10)));
        atom.Append(Snapshot(t0.AddSeconds(20)));

        var rows = atom.GetWindow(t0.AddMinutes(1), TimeSpan.FromMinutes(2));
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].TimestampUtc <= rows[1].TimestampUtc);
        Assert.True(rows[1].TimestampUtc <= rows[2].TimestampUtc);
    }

    [Fact]
    public void Ring_overwrites_oldest_when_capacity_exceeded()
    {
        var atom = NewAtom(capacity: 3);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 7; i++)
            atom.Append(Snapshot(t0.AddSeconds(i * 10), latency: 100 + i));

        var rows = atom.GetWindow(t0.AddHours(1), TimeSpan.FromHours(1));
        // Only the last 3 samples survive (i = 4, 5, 6).
        Assert.Equal(3, rows.Count);
        Assert.Equal(104, rows[0].LatencyP95Ms);
        Assert.Equal(105, rows[1].LatencyP95Ms);
        Assert.Equal(106, rows[2].LatencyP95Ms);
    }

    [Fact]
    public void GetWindow_filters_to_requested_span()
    {
        var atom = NewAtom(capacity: 100);
        var now = DateTime.UtcNow;
        atom.Append(Snapshot(now.AddHours(-2)));  // outside 1h window
        atom.Append(Snapshot(now.AddMinutes(-30))); // inside
        atom.Append(Snapshot(now.AddMinutes(-5)));  // inside

        var rows = atom.GetWindow(now, TimeSpan.FromHours(1));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void GetWindow_returns_everything_when_window_is_zero()
    {
        // The endpoint passes the requested window; callers that want the
        // whole ring (e.g. integration tests) pass TimeSpan.Zero -- the
        // empty-state branch in the view component depends on this.
        var atom = NewAtom(capacity: 5);
        var t0 = DateTime.UtcNow;
        atom.Append(Snapshot(t0.AddHours(-24)));
        atom.Append(Snapshot(t0));

        var rows = atom.GetWindow(t0, TimeSpan.Zero);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Capacity_property_reflects_options_value()
    {
        var atom = NewAtom(capacity: 42);
        Assert.Equal(42, atom.Capacity);
    }

    [Fact]
    public void Options_value_below_one_clamps_to_one()
    {
        // Defensive: tests / DI typos that set Capacity=0 must not blow up
        // the ring buffer's modulo math at runtime.
        var atom = NewAtom(capacity: 0);
        Assert.Equal(1, atom.Capacity);
        atom.Append(Snapshot(DateTime.UtcNow));
        Assert.Single(atom.GetWindow(DateTime.UtcNow, TimeSpan.FromHours(1)));
    }

    private static DegradationHistoryAtom NewAtom(int capacity)
    {
        var opts = Options.Create(new SiteHealthHistoryOptions { Capacity = capacity });
        return new DegradationHistoryAtom(opts);
    }

    private static DegradationSnapshot Snapshot(DateTime t, double latency = 50)
        => new(
            TimestampUtc: t,
            Latency5xxRate: 0,
            Latency4xxRate: 0,
            Latency429Rate: 0,
            LatencyP50Ms: latency,
            LatencyP95Ms: latency,
            NotFoundRate: 0);
}