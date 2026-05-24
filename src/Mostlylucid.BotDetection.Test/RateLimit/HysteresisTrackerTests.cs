using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the "true for N seconds" dwell-gate that <see cref="AdaptiveScalingTracker"/>
///     uses to damp tier transitions. A single-request 5xx spike must not
///     halve bot allowance; that's the whole point of this primitive.
/// </summary>
public class HysteresisTrackerTests
{
    [Fact]
    public void FirstCallWithTrue_DoesNotSatisfyYet()
    {
        var clock = new FrozenClock();
        var tracker = new HysteresisTracker(() => clock.Now);

        Assert.False(tracker.IsSatisfied("rule", conditionTrue: true, forSeconds: 30));
    }

    [Fact]
    public void TrueForDwellSeconds_Satisfies()
    {
        var clock = new FrozenClock();
        var tracker = new HysteresisTracker(() => clock.Now);

        Assert.False(tracker.IsSatisfied("rule", true, forSeconds: 30));  // first true
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.IsSatisfied("rule", true, forSeconds: 30));   // dwell elapsed
    }

    [Fact]
    public void FalseResetsDwell()
    {
        var clock = new FrozenClock();
        var tracker = new HysteresisTracker(() => clock.Now);

        tracker.IsSatisfied("rule", true, forSeconds: 30);
        clock.Advance(TimeSpan.FromSeconds(20));
        tracker.IsSatisfied("rule", false, forSeconds: 30);  // resets

        clock.Advance(TimeSpan.FromSeconds(11));               // would be 31s from original
        Assert.False(tracker.IsSatisfied("rule", true, forSeconds: 30));  // but only 11s after reset
    }

    [Fact]
    public void DifferentRules_HaveIndependentDwell()
    {
        var clock = new FrozenClock();
        var tracker = new HysteresisTracker(() => clock.Now);

        tracker.IsSatisfied("rule-a", true, forSeconds: 30);
        clock.Advance(TimeSpan.FromSeconds(31));
        tracker.IsSatisfied("rule-b", true, forSeconds: 30);   // b just started

        Assert.True(tracker.IsSatisfied("rule-a", true, forSeconds: 30));
        Assert.False(tracker.IsSatisfied("rule-b", true, forSeconds: 30));
    }

    private sealed class FrozenClock
    {
        public DateTime Now { get; private set; } = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan delta) => Now += delta;
    }
}
