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

    // === TTL + cardinality cap ============================================
    //
    // Background: the store originally kept buckets forever. That's fine for
    // IP-keyed traffic (bounded by routable IP space) but becomes a memory
    // leak the moment you key on a rotating signature, per-fingerprint
    // identity, or per-session id. Phase 1A pins the lazy-sweep + LFU-cap
    // contract before adding it.

    [Fact]
    public void Sweep_RemovesIdleBuckets_OlderThanIdleTtl()
    {
        var clock = new FrozenClock();
        var opts = new InMemoryTokenBucketStoreOptions
        {
            IdleTtl = TimeSpan.FromMinutes(10),
            SweepEveryN = 1, // sweep every call so the test is deterministic
            MaxBuckets = 0,  // disable cap; isolating TTL
        };
        var store = new InMemoryTokenBucketStore(() => clock.Now, opts);

        store.TryConsume("p", "sig:idle", 10, 60);
        Assert.NotNull(store.Peek("p", "sig:idle"));

        // Advance past IdleTtl; touch a different key to trigger the sweep.
        clock.Advance(TimeSpan.FromMinutes(11));
        store.TryConsume("p", "sig:active", 10, 60);

        Assert.Null(store.Peek("p", "sig:idle"));
        Assert.NotNull(store.Peek("p", "sig:active"));
    }

    [Fact]
    public void Sweep_DoesNotEvict_RecentlyTouchedBuckets()
    {
        var clock = new FrozenClock();
        var opts = new InMemoryTokenBucketStoreOptions
        {
            IdleTtl = TimeSpan.FromMinutes(10),
            SweepEveryN = 1,
            MaxBuckets = 0,
        };
        var store = new InMemoryTokenBucketStore(() => clock.Now, opts);

        store.TryConsume("p", "sig:a", 10, 60);
        clock.Advance(TimeSpan.FromMinutes(9));     // still inside TTL
        store.TryConsume("p", "sig:b", 10, 60);     // also triggers a sweep

        Assert.NotNull(store.Peek("p", "sig:a"));
        Assert.NotNull(store.Peek("p", "sig:b"));
    }

    [Fact]
    public void Sweep_EnforcesMaxBuckets_EvictingOldestFirst()
    {
        var clock = new FrozenClock();
        var opts = new InMemoryTokenBucketStoreOptions
        {
            IdleTtl = TimeSpan.FromHours(1), // disable TTL; isolating MaxBuckets
            SweepEveryN = 1,
            MaxBuckets = 3,
        };
        var store = new InMemoryTokenBucketStore(() => clock.Now, opts);

        // Insert 5 keys with monotonically advancing LastRefillUtc.
        for (var i = 0; i < 5; i++)
        {
            store.TryConsume("p", $"sig:{i}", 10, 60);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // After the cap-enforcing sweep on the 5th insert, only the 3 newest
        // should remain. sig:0 and sig:1 are the two oldest.
        Assert.Null(store.Peek("p", "sig:0"));
        Assert.Null(store.Peek("p", "sig:1"));
        Assert.NotNull(store.Peek("p", "sig:2"));
        Assert.NotNull(store.Peek("p", "sig:3"));
        Assert.NotNull(store.Peek("p", "sig:4"));
    }

    [Fact]
    public void Sweep_HappensLazilyEveryNthCall_NotEveryCall()
    {
        var clock = new FrozenClock();
        var opts = new InMemoryTokenBucketStoreOptions
        {
            IdleTtl = TimeSpan.FromMinutes(10),
            SweepEveryN = 10,
            MaxBuckets = 0,
        };
        var store = new InMemoryTokenBucketStore(() => clock.Now, opts);

        // Seed an idle bucket, advance past TTL.
        store.TryConsume("p", "sig:idle", 10, 60);
        clock.Advance(TimeSpan.FromMinutes(11));

        // 8 more calls (total 9 including the seed) shouldn't have triggered a
        // sweep yet -- the idle bucket should still be there.
        for (var i = 0; i < 8; i++)
            store.TryConsume("p", $"sig:probe{i}", 10, 60);
        Assert.NotNull(store.Peek("p", "sig:idle"));

        // The 10th call (counter % 10 == 0) triggers the sweep.
        store.TryConsume("p", "sig:trigger", 10, 60);
        Assert.Null(store.Peek("p", "sig:idle"));
    }

    private sealed class FrozenClock
    {
        public DateTime Now { get; private set; } = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan delta) => Now += delta;
    }
}
