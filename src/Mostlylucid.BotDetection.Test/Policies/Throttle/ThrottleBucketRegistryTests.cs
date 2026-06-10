using Mostlylucid.BotDetection.Policies.Throttle;

namespace Mostlylucid.BotDetection.Test.Policies.Throttle;

/// <summary>
///     Coverage for <see cref="ThrottleBucketRegistry"/> -- the G token-bucket
///     enforcer. The registry holds one bucket per scope key and admits or
///     denies requests against a steady-state cap in requests/sec. Time is
///     driven explicitly via the <c>now</c> argument; no wall-clock.
/// </summary>
public sealed class ThrottleBucketRegistryTests
{
    [Fact]
    public void Bucket_admits_burst_up_to_capacity_then_denies()
    {
        var registry = new ThrottleBucketRegistry();
        var t = DateTimeOffset.UnixEpoch;

        // 5 RPS capacity = 5 tokens at boot. Admit five back-to-back.
        for (var i = 0; i < 5; i++)
            Assert.True(registry.TryAcquire("scope:a", 5, t));

        // Sixth request at the same instant is denied.
        Assert.False(registry.TryAcquire("scope:a", 5, t));
    }

    [Fact]
    public void Bucket_refills_linearly_over_time()
    {
        var registry = new ThrottleBucketRegistry();
        var t = DateTimeOffset.UnixEpoch;

        // Drain the bucket.
        for (var i = 0; i < 5; i++)
            Assert.True(registry.TryAcquire("scope:b", 5, t));
        Assert.False(registry.TryAcquire("scope:b", 5, t));

        // Half a second later, 2.5 tokens have refilled.
        Assert.True(registry.TryAcquire("scope:b", 5, t + TimeSpan.FromMilliseconds(500)));
        Assert.True(registry.TryAcquire("scope:b", 5, t + TimeSpan.FromMilliseconds(500)));
        Assert.False(registry.TryAcquire("scope:b", 5, t + TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Buckets_are_independent_per_scope_key()
    {
        var registry = new ThrottleBucketRegistry();
        var t = DateTimeOffset.UnixEpoch;

        // Drain scope:a.
        for (var i = 0; i < 3; i++)
            Assert.True(registry.TryAcquire("scope:a", 3, t));
        Assert.False(registry.TryAcquire("scope:a", 3, t));

        // scope:b is untouched.
        for (var i = 0; i < 3; i++)
            Assert.True(registry.TryAcquire("scope:b", 3, t));
    }

    [Fact]
    public void Refill_caps_at_capacity_no_runaway_credit()
    {
        var registry = new ThrottleBucketRegistry();
        var t = DateTimeOffset.UnixEpoch;

        // Drain.
        for (var i = 0; i < 5; i++)
            Assert.True(registry.TryAcquire("scope:c", 5, t));

        // Ten minutes later there are at most 5 tokens, not 5 * 600.
        var later = t + TimeSpan.FromMinutes(10);
        for (var i = 0; i < 5; i++)
            Assert.True(registry.TryAcquire("scope:c", 5, later));
        Assert.False(registry.TryAcquire("scope:c", 5, later));
    }

    [Fact]
    public void Zero_or_negative_rate_throws()
    {
        var registry = new ThrottleBucketRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.TryAcquire("scope:x", 0, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.TryAcquire("scope:x", -1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Empty_or_null_scope_key_throws()
    {
        var registry = new ThrottleBucketRegistry();
        Assert.Throws<ArgumentException>(
            () => registry.TryAcquire("", 5, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(
            () => registry.TryAcquire("   ", 5, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Rate_change_at_runtime_takes_effect_on_next_acquire()
    {
        var registry = new ThrottleBucketRegistry();
        var t = DateTimeOffset.UnixEpoch;

        // Bucket minted at 10 RPS.
        for (var i = 0; i < 10; i++)
            Assert.True(registry.TryAcquire("scope:rate", 10, t));
        Assert.False(registry.TryAcquire("scope:rate", 10, t));

        // After one second the bucket has refilled at 10/sec back to capacity.
        // Drop the rate to 2 RPS -- existing leftovers are capped down to 2.
        for (var i = 0; i < 2; i++)
            Assert.True(registry.TryAcquire("scope:rate", 2, t + TimeSpan.FromSeconds(1)));
        Assert.False(registry.TryAcquire("scope:rate", 2, t + TimeSpan.FromSeconds(1)));
    }
}
