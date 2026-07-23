using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     End-to-end pin for the phase-4 adaptive-scaling pipeline. Threads
///     <see cref="DegradationAtom"/> + <see cref="HysteresisTracker"/> through
///     <see cref="AdaptiveScalingTracker"/> and asserts the multiplier
///     responds to upstream-health changes (and *only* after the dwell
///     gate is satisfied).
/// </summary>
public class AdaptiveScalingTrackerTests
{
    [Fact]
    public void Disabled_AlwaysReturnsNominalMultiplier()
    {
        var (tracker, _, _) = Build(new AdaptiveScalingOptions { Enabled = false });
        Assert.Equal(1.0, tracker.CurrentMultiplier);
        Assert.Null(tracker.CurrentTier);
    }

    [Fact]
    public void NoDegradation_StaysAtBaseline()
    {
        var (tracker, _, _) = Build(DefaultOptions());
        Assert.Equal(1.0, tracker.CurrentMultiplier);
        Assert.Null(tracker.CurrentTier);
    }

    [Fact]
    public void ThresholdExceededButDwellNotMet_StaysAtBaseline()
    {
        var (tracker, atom, _) = Build(DefaultOptions());

        // Spike 5xx so the "degraded" threshold (3%) is exceeded on first
        // sample. The tracker MUST NOT enter the tier until DwellSeconds
        // of continuous condition.
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(500, latencyMs: 10, path: "/");

        Assert.Equal(1.0, tracker.CurrentMultiplier);
    }

    [Fact]
    public void ThresholdExceededAndDwellMet_AppliesTierMultiplier()
    {
        // Latency EMA between degraded (1000ms) and critical (2000ms)
        // -- 1500ms with healthy status codes lands in degraded cleanly.
        var clock = new TestClock();
        var (tracker, atom, _) = Build(DefaultOptions(), clock);

        for (var i = 0; i < 100; i++)
            atom.RecordResponse(200, latencyMs: 1500, path: "/");
        _ = tracker.CurrentMultiplier;
        clock.Advance(TimeSpan.FromSeconds(31));

        // Degraded tier: P95 latency >= 1000ms, multiplier 0.5
        Assert.Equal(0.5, tracker.CurrentMultiplier);
        Assert.Equal("degraded", tracker.CurrentTier);
    }

    [Fact]
    public void CriticalThresholdExceededAndDwellMet_AppliesCriticalMultiplier()
    {
        var clock = new TestClock();
        var (tracker, atom, _) = Build(DefaultOptions(), clock);

        // 5xx EMA approaches 1.0 -> definitely >= 10% (critical threshold).
        for (var i = 0; i < 200; i++)
            atom.RecordResponse(500, latencyMs: 10, path: "/");
        _ = tracker.CurrentMultiplier;
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(0.1, tracker.CurrentMultiplier);
        Assert.Equal("critical", tracker.CurrentTier);
    }

    [Fact]
    public void Recovery_StepsBackTowardBaseline()
    {
        var clock = new TestClock();
        var opts = DefaultOptions();
        var (tracker, atom, _) = Build(opts, clock);

        // Push into degraded.
        for (var i = 0; i < 200; i++)
            atom.RecordResponse(500, latencyMs: 10, path: "/");
        _ = tracker.CurrentMultiplier;
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0.1, tracker.CurrentMultiplier);  // critical

        // Health recovers: feed a long stream of 2xx so EMAs drop below all thresholds.
        for (var i = 0; i < 2000; i++)
            atom.RecordResponse(200, latencyMs: 50, path: "/");

        // First read after recovery applies RecoveryMultiplier (0.8 by default).
        // From 0.1, the next read should be 0.1 / 0.8 = 0.125. Successive
        // reads ramp toward 1.0 (we don't drop straight to baseline).
        var step1 = tracker.CurrentMultiplier;
        Assert.True(step1 > 0.1 && step1 < 1.0,
            $"Expected gradual recovery (0.1 -> ~0.125), got {step1}");
    }

    [Fact]
    public void HysteresisDamps_ShortBurstThatRecovers()
    {
        // The classic flap case: 5xx briefly spikes, then the origin recovers
        // within the dwell window. The tracker must NOT enter the tier at all.
        var clock = new TestClock();
        var (tracker, atom, _) = Build(DefaultOptions(), clock);

        for (var i = 0; i < 10; i++) atom.RecordResponse(500, 10, "/");
        _ = tracker.CurrentMultiplier;
        clock.Advance(TimeSpan.FromSeconds(15));         // halfway through dwell
        for (var i = 0; i < 200; i++) atom.RecordResponse(200, 10, "/");  // 5xx EMA collapses
        clock.Advance(TimeSpan.FromSeconds(20));         // past original dwell window

        Assert.Equal(1.0, tracker.CurrentMultiplier);
        Assert.Null(tracker.CurrentTier);
    }

    // ===== helpers =====

    private static AdaptiveScalingOptions DefaultOptions() => new()
    {
        Enabled = true,
        Tiers =
        [
            new() { Name = "nominal",  P95LatencyMs =  500, Error5xxRatePct =  1, BotMultiplier = 1.0 },
            new() { Name = "degraded", P95LatencyMs = 1000, Error5xxRatePct =  3, BotMultiplier = 0.5 },
            new() { Name = "critical", P95LatencyMs = 2000, Error5xxRatePct = 10, BotMultiplier = 0.1 },
        ],
        Hysteresis = new() { DwellSeconds = 30, RecoveryMultiplier = 0.8 },
    };

    private static (AdaptiveScalingTracker tracker, DegradationAtom atom, HysteresisTracker hys)
        Build(AdaptiveScalingOptions options, TestClock? clock = null)
    {
        var atom = new DegradationAtom();
        var hys = clock is null
            ? new HysteresisTracker()
            : new HysteresisTracker(() => clock.Now);
        var tracker = new AdaptiveScalingTracker(
            atom, hys, new StaticMonitor<AdaptiveScalingOptions>(options),
            NullLogger<AdaptiveScalingTracker>.Instance);
        return (tracker, atom, hys);
    }

    private sealed class TestClock
    {
        public DateTime Now { get; private set; } = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan delta) => Now += delta;
    }

    private sealed class StaticMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}
