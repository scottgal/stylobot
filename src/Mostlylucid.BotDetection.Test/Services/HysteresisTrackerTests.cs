using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class HysteresisTrackerTests
{
    [Fact]
    public void IsSatisfied_ConditionNotYetTrue_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: false, forSeconds: 30.0));
    }

    [Fact]
    public void IsSatisfied_ConditionJustBecameTrue_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }

    [Fact]
    public void IsSatisfied_ConditionTrueForLongEnough_ReturnsTrue()
    {
        var tracker = new HysteresisTracker();
        tracker.ForceFirstTrue("rule-1", DateTime.UtcNow.AddSeconds(-35));

        Assert.True(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }

    [Fact]
    public void IsSatisfied_ConditionFalseAfterBeingTrue_ResetsTimer()
    {
        var tracker = new HysteresisTracker();
        tracker.ForceFirstTrue("rule-1", DateTime.UtcNow.AddSeconds(-35));
        Assert.True(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));

        // Condition drops to false - timer resets
        tracker.IsSatisfied("rule-1", conditionTrue: false, forSeconds: 30.0);

        // Check again with condition true - timer just reset so not yet satisfied
        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }

    [Fact]
    public void Reset_ClearsAllTimers()
    {
        var tracker = new HysteresisTracker();
        tracker.ForceFirstTrue("rule-1", DateTime.UtcNow.AddSeconds(-35));
        tracker.Reset();

        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }
}
