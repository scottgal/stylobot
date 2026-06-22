using Mostlylucid.BotDetection.Identity.Triggers;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pure-function coverage for <see cref="AdaptiveTriggerEvaluator.Evaluate"/>.
///     One test per gate so a regression to a single rule shows up in isolation.
///     See <c>docs/superpowers/specs/2026-06-21-adaptive-learning-loop-design.md</c>
///     §2.A for the spec these tests pin.
/// </summary>
public sealed class AdaptiveTriggerEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 21, 13, 0, 0, TimeSpan.Zero);

    private static TriggerContext Ctx(
        DateTimeOffset? lastRun = null,
        LoadBand band = LoadBand.Low,
        Dictionary<string, double>? signals = null)
        => new(
            Now: Now,
            LastSuccessfulRunUtc: lastRun,
            CurrentBand: band,
            Signals: signals ?? new Dictionary<string, double>());

    [Fact]
    public void Disabled_policy_never_fires()
    {
        var policy = new AdaptiveTriggerPolicy { Enabled = false, MinInterval = TimeSpan.Zero };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy, Ctx());
        Assert.False(decision.ShouldRun);
        Assert.Equal("disabled", decision.Reason);
    }

    [Fact]
    public void First_run_after_startup_fires_via_safety_net()
    {
        // LastSuccessfulRunUtc = null means "never ran" -> elapsed = TimeSpan.MaxValue,
        // which trips the safety net before signal evaluation runs. This is the
        // demo path: the first heartbeat after startup runs calibration even
        // with no signal pressure yet, so operators see the loop wake up.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(30),
            MaxInterval = TimeSpan.FromHours(6),
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 1) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(signals: new() { ["obs.unabsorbed"] = 5 }));
        Assert.True(decision.ShouldRun);
        Assert.Contains("safety net", decision.Reason);
    }

    [Fact]
    public void First_run_after_startup_with_no_safety_net_falls_to_signal()
    {
        // When MaxInterval is null (pure signal mode), "never ran" still skips
        // the min-interval gate (elapsed = infinity > min) and the signal
        // condition is the actual trigger reason.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(30),
            MaxInterval = null,
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 1) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(signals: new() { ["obs.unabsorbed"] = 5 }));
        Assert.True(decision.ShouldRun);
        Assert.Contains("signal obs.unabsorbed=5", decision.Reason);
    }

    [Fact]
    public void Min_interval_gate_blocks_repeat_fires()
    {
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromMinutes(5),
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 1) },
        };
        // Last run 30s ago; min interval 5m. Even with a screaming signal,
        // we MUST hold.
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddSeconds(-30), signals: new() { ["obs.unabsorbed"] = 1000 }));
        Assert.False(decision.ShouldRun);
        Assert.Contains("min interval", decision.Reason);
    }

    [Fact]
    public void Max_interval_safety_net_fires_with_no_signal()
    {
        // No signal at all, but the safety net has elapsed. Calibration must
        // run anyway so a quiet system still recalibrates eventually.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(30),
            MaxInterval = TimeSpan.FromHours(1),
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 1) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddHours(-2)));
        Assert.True(decision.ShouldRun);
        Assert.Contains("safety net", decision.Reason);
    }

    [Fact]
    public void Band_ceiling_blocks_even_with_signal()
    {
        // System is under heavy load. Signal says fire; band ceiling says no.
        // The ceiling wins -- we'd rather defer learning than starve the
        // request-handling path.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(1),
            MaxBand = LoadBand.Normal,
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 1) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddSeconds(-30), band: LoadBand.High,
                signals: new() { ["obs.unabsorbed"] = 1000 }));
        Assert.False(decision.ShouldRun);
        Assert.Contains("band High > ceiling Normal", decision.Reason);
    }

    [Fact]
    public void Below_band_ceiling_allows_signal_fire()
    {
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(1),
            MaxBand = LoadBand.Normal,
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 50) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddSeconds(-30), band: LoadBand.Low,
                signals: new() { ["obs.unabsorbed"] = 73 }));
        Assert.True(decision.ShouldRun);
    }

    [Fact]
    public void Below_threshold_signal_does_not_fire()
    {
        // 12 obs, threshold 50. Signal axis says "not yet"; no other axes met.
        // Decision is NO with a reason that names the unmet condition.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(1),
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 50) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddSeconds(-30), signals: new() { ["obs.unabsorbed"] = 12 }));
        Assert.False(decision.ShouldRun);
        Assert.Equal("no signal condition met", decision.Reason);
    }

    [Fact]
    public void Multiple_signal_conditions_OR_first_match_wins_reason()
    {
        // Both thresholds met; the reason should name the first one (obs)
        // because AnyOfSignals iterates in declaration order. Pins the
        // evaluation order so the /admin/learning/health output is stable.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(1),
            AnyOfSignals = new[]
            {
                new SignalCondition("obs.unabsorbed", 50),
                new SignalCondition("drift.l2", 0.5),
            },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddSeconds(-30),
                signals: new() { ["obs.unabsorbed"] = 73, ["drift.l2"] = 1.2 }));
        Assert.True(decision.ShouldRun);
        Assert.Contains("obs.unabsorbed", decision.Reason);
    }

    [Fact]
    public void Missing_signal_key_skips_that_condition_without_firing()
    {
        // Policy expects "drift.l2" but signals dict only has "obs.unabsorbed"
        // which doesn't satisfy ANY listed condition. Decision: NO. Important
        // because future signal sources might add/remove keys at runtime.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(1),
            AnyOfSignals = new[] { new SignalCondition("drift.l2", 0.5) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddSeconds(-30),
                signals: new() { ["obs.unabsorbed"] = 1000 }));
        Assert.False(decision.ShouldRun);
    }

    [Fact]
    public void Null_max_interval_disables_safety_net()
    {
        // No MaxInterval, no signal, last run was an hour ago. Decision: NO.
        // Pure signal-driven mode never fires on time alone.
        var policy = new AdaptiveTriggerPolicy
        {
            MinInterval = TimeSpan.FromSeconds(30),
            MaxInterval = null,
            AnyOfSignals = new[] { new SignalCondition("obs.unabsorbed", 1) },
        };
        var decision = AdaptiveTriggerEvaluator.Evaluate(policy,
            Ctx(lastRun: Now.AddHours(-1)));
        Assert.False(decision.ShouldRun);
    }
}
