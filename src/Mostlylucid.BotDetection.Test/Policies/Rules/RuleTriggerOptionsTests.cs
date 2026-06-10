using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Pure data-shape tests for <see cref="RuleTriggerOptions"/>. The runtime
///     state machine lives in MeterTriggerService (next task) -- these tests
///     pin the contract the runtime depends on: defaults coalesce, explicit
///     values pass through, value equality holds.
/// </summary>
public class RuleTriggerOptionsTests
{
    [Fact]
    public void Default_construction_yields_spec_defaults()
    {
        var opts = new RuleTriggerOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), opts.EffectiveSustainFor);
        Assert.Equal(TimeSpan.FromSeconds(60), opts.EffectiveRecoverAfter);
        Assert.Null(opts.ActionWhileArmed);
    }

    [Fact]
    public void Explicit_non_default_values_pass_through_unchanged()
    {
        var opts = new RuleTriggerOptions(
            SustainFor: TimeSpan.FromSeconds(15),
            RecoverAfter: TimeSpan.FromMinutes(2),
            ActionWhileArmed: new PolicyAction.Throttle(5));

        Assert.Equal(TimeSpan.FromSeconds(15), opts.EffectiveSustainFor);
        Assert.Equal(TimeSpan.FromMinutes(2), opts.EffectiveRecoverAfter);
        Assert.IsType<PolicyAction.Throttle>(opts.ActionWhileArmed);
    }

    [Fact]
    public void Zero_sustain_falls_back_to_spec_default()
    {
        var opts = new RuleTriggerOptions(
            SustainFor: TimeSpan.Zero,
            RecoverAfter: TimeSpan.FromSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(30), opts.EffectiveSustainFor);
        Assert.Equal(TimeSpan.FromSeconds(90), opts.EffectiveRecoverAfter);
    }

    [Fact]
    public void Value_equality_round_trips_via_record_equality()
    {
        var a = new RuleTriggerOptions(
            SustainFor: TimeSpan.FromSeconds(45),
            RecoverAfter: TimeSpan.FromSeconds(90),
            ActionWhileArmed: new PolicyAction.Throttle(10, "armed"));
        var b = new RuleTriggerOptions(
            SustainFor: TimeSpan.FromSeconds(45),
            RecoverAfter: TimeSpan.FromSeconds(90),
            ActionWhileArmed: new PolicyAction.Throttle(10, "armed"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Differing_armed_action_breaks_equality()
    {
        var a = new RuleTriggerOptions(ActionWhileArmed: new PolicyAction.Throttle(5));
        var b = new RuleTriggerOptions(ActionWhileArmed: new PolicyAction.Throttle(10));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Spec_defaults_are_published_constants()
    {
        // Pin the spec values so a future tweak shows up in a test diff first.
        Assert.Equal(TimeSpan.FromSeconds(30), RuleTriggerOptions.DefaultSustainFor);
        Assert.Equal(TimeSpan.FromSeconds(60), RuleTriggerOptions.DefaultRecoverAfter);
    }
}
