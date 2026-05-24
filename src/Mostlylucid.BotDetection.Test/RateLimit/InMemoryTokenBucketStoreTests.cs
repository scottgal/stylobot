using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the token-bucket math for the in-memory store -- the hot path
///     that every <see cref="Actions.RateLimitActionPolicy"/> call routes
///     through. Phase 2 of the policy-grammar work.
/// </summary>
public class InMemoryTokenBucketStoreTests
{
    [Fact]
    public void FirstRequest_StartsBucketAtCapacityAndConsumesOne()
    {
        var store = new InMemoryTokenBucketStore();

        var allowed = store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60);

        Assert.True(allowed);
        var snap = store.Peek("p", "sig:a");
        Assert.NotNull(snap);
        // 10 capacity, one consumed -> 9 left.
        Assert.Equal(9.0, snap!.TokensRemaining, precision: 1);
    }

    [Fact]
    public void BurstConsumption_ExhaustsBucket_ThenDenies()
    {
        var clock = new FrozenClock();
        var store = new InMemoryTokenBucketStore(() => clock.Now);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60),
                $"Request {i} should have been allowed (within burst capacity)");
        }

        // 11th request without any time passing should fail.
        Assert.False(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60));
    }

    [Fact]
    public void TokensRefill_OverTime_AtConfiguredRate()
    {
        var clock = new FrozenClock();
        var store = new InMemoryTokenBucketStore(() => clock.Now);

        // 60/min = 1/sec. Burn the entire 10-capacity bucket.
        for (var i = 0; i < 10; i++)
            store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60);
        Assert.False(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60));

        // Advance 5 seconds -- should have 5 fresh tokens.
        clock.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 5; i++)
            Assert.True(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60),
                $"Refill request {i} should have been allowed");

        // 6th refill request in the same instant should fail.
        Assert.False(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60));
    }

    [Fact]
    public void RefillCappedAtCapacity_LongIdleDoesntCreateUnboundedCredit()
    {
        var clock = new FrozenClock();
        var store = new InMemoryTokenBucketStore(() => clock.Now);

        store.TryConsume("p", "sig:a", capacity: 5, refillRatePerMinute: 60);  // first request, bucket -> 4
        clock.Advance(TimeSpan.FromHours(1));                                  // would refill +3600 tokens

        // Should only see capacity (5) tokens available -- 5 consecutive allows, 6th denies.
        for (var i = 0; i < 5; i++)
            Assert.True(store.TryConsume("p", "sig:a", capacity: 5, refillRatePerMinute: 60),
                $"Request {i} after long idle should have been allowed");
        Assert.False(store.TryConsume("p", "sig:a", capacity: 5, refillRatePerMinute: 60));
    }

    [Fact]
    public void BucketsAreIsolated_ByPolicyName()
    {
        var clock = new FrozenClock();
        var store = new InMemoryTokenBucketStore(() => clock.Now);

        // Exhaust policy A's bucket for sig:a.
        for (var i = 0; i < 10; i++)
            store.TryConsume("policy-a", "sig:a", capacity: 10, refillRatePerMinute: 60);
        Assert.False(store.TryConsume("policy-a", "sig:a", capacity: 10, refillRatePerMinute: 60));

        // Policy B for the same key should still be at capacity.
        Assert.True(store.TryConsume("policy-b", "sig:a", capacity: 10, refillRatePerMinute: 60));
    }

    [Fact]
    public void BucketsAreIsolated_ByKey()
    {
        var clock = new FrozenClock();
        var store = new InMemoryTokenBucketStore(() => clock.Now);

        // Exhaust sig:a's bucket.
        for (var i = 0; i < 10; i++)
            store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60);
        Assert.False(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 60));

        // sig:b is independent.
        Assert.True(store.TryConsume("p", "sig:b", capacity: 10, refillRatePerMinute: 60));
    }

    [Fact]
    public void MisconfiguredZeroOrNegative_AllowsFailOpen()
    {
        var store = new InMemoryTokenBucketStore();
        // capacity 0 or RPM 0 = "open"; a misconfig shouldn't lock out every visitor.
        Assert.True(store.TryConsume("p", "sig:a", capacity: 0, refillRatePerMinute: 60));
        Assert.True(store.TryConsume("p", "sig:a", capacity: 10, refillRatePerMinute: 0));
    }

    [Fact]
    public void Peek_UnknownKey_ReturnsNull()
    {
        var store = new InMemoryTokenBucketStore();
        Assert.Null(store.Peek("p", "nope"));
    }

    private sealed class FrozenClock
    {
        public DateTime Now { get; private set; } = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan delta) => Now += delta;
    }
}
