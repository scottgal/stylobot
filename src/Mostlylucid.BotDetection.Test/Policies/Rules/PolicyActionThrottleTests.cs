using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Pure data-shape tests for <see cref="PolicyAction.Throttle"/>. The
///     pipeline enforcement (token bucket keyed by rule scope) lands in the
///     runtime task; these tests pin the value-record contract: positive RPS
///     required, value equality holds, reason round-trips.
/// </summary>
public class PolicyActionThrottleTests
{
    [Fact]
    public void Construction_with_positive_rps_succeeds()
    {
        var action = new PolicyAction.Throttle(50, "armed-trigger");
        Assert.Equal(50, action.RequestsPerSecond);
        Assert.Equal("armed-trigger", action.Reason);
    }

    [Fact]
    public void Reason_defaults_to_null_when_omitted()
    {
        var action = new PolicyAction.Throttle(10);
        Assert.Equal(10, action.RequestsPerSecond);
        Assert.Null(action.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Construction_with_non_positive_rps_throws(int rps)
    {
        // Mirrors how RateLimit's validation is asserted elsewhere -- positional
        // record arguments get coerced via ArgumentOutOfRangeException so the
        // factory site fails fast rather than silently arming a zero-cap.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyAction.Throttle(rps));
    }

    [Fact]
    public void Value_equality_round_trips_via_record_equality()
    {
        var a = new PolicyAction.Throttle(25, "burst");
        var b = new PolicyAction.Throttle(25, "burst");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_rps_breaks_equality()
    {
        Assert.NotEqual(new PolicyAction.Throttle(10), new PolicyAction.Throttle(20));
    }

    [Fact]
    public void Different_reason_breaks_equality()
    {
        Assert.NotEqual(
            new PolicyAction.Throttle(10, "alpha"),
            new PolicyAction.Throttle(10, "beta"));
    }

    [Fact]
    public void Throttle_is_a_PolicyAction_subtype()
    {
        PolicyAction action = new PolicyAction.Throttle(5);
        Assert.IsType<PolicyAction.Throttle>(action);
    }
}
