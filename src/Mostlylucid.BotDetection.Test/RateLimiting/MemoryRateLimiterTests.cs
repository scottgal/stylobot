using Mostlylucid.BotDetection.RateLimiting;
using Xunit;

namespace Mostlylucid.BotDetection.Test.RateLimiting;

public class MemoryRateLimiterTests
{
    [Fact]
    public void Burst_is_available_immediately_then_refill_rate_caps()
    {
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var config = new RateLimitConfig(RequestsPerMinute: 60, BurstSize: 10);
        var subjects = new[] { new RateLimitSubject(RateLimitSubjectKind.BotType, "Scraper") };

        // Drain the burst -- 10 tokens available at start.
        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire(subjects, config), $"expected token {i + 1} of 10");

        // 11th must fail: bucket is empty and no real time has passed.
        Assert.False(limiter.TryAcquire(subjects, config), "burst exhausted; further acquires must fail");
    }

    [Fact]
    public void Refill_replenishes_one_token_per_second_at_RequestsPerMinute_60()
    {
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var config = new RateLimitConfig(RequestsPerMinute: 60, BurstSize: 1);
        var subjects = new[] { new RateLimitSubject(RateLimitSubjectKind.BotType, "AI") };

        // Drain the single token.
        Assert.True(limiter.TryAcquire(subjects, config));
        Assert.False(limiter.TryAcquire(subjects, config));

        // Wait > 1s for refill. 1.2s leaves headroom so the test is not flaky
        // on slow CI runners.
        Thread.Sleep(1200);
        Assert.True(limiter.TryAcquire(subjects, config), "expected the refilled token");
    }

    [Fact]
    public void Distinct_subjects_have_independent_buckets()
    {
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var config = new RateLimitConfig(RequestsPerMinute: 60, BurstSize: 2);

        var scraper = new[] { new RateLimitSubject(RateLimitSubjectKind.BotType, "Scraper") };
        var ai      = new[] { new RateLimitSubject(RateLimitSubjectKind.BotType, "AI") };

        // Drain Scraper's bucket.
        Assert.True(limiter.TryAcquire(scraper, config));
        Assert.True(limiter.TryAcquire(scraper, config));
        Assert.False(limiter.TryAcquire(scraper, config));

        // AI's bucket is untouched.
        Assert.True(limiter.TryAcquire(ai, config));
        Assert.True(limiter.TryAcquire(ai, config));
    }

    [Fact]
    public void Composite_subject_key_is_order_independent()
    {
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var config = new RateLimitConfig(RequestsPerMinute: 60, BurstSize: 1);

        var s1 = new[] {
            new RateLimitSubject(RateLimitSubjectKind.BotType, "AI"),
            new RateLimitSubject(RateLimitSubjectKind.Country, "US"),
        };
        var s2 = new[] {
            new RateLimitSubject(RateLimitSubjectKind.Country, "US"),
            new RateLimitSubject(RateLimitSubjectKind.BotType, "AI"),
        };

        Assert.True(limiter.TryAcquire(s1, config));
        // The two subject orderings must hit the same bucket.
        Assert.False(limiter.TryAcquire(s2, config));
    }

    [Fact]
    public void Peek_does_not_refill()
    {
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var config = new RateLimitConfig(RequestsPerMinute: 60, BurstSize: 5);
        var subjects = new[] { new RateLimitSubject(RateLimitSubjectKind.Country, "RU") };

        // Drain 3 tokens; bucket is at 2.
        limiter.TryAcquire(subjects, config);
        limiter.TryAcquire(subjects, config);
        limiter.TryAcquire(subjects, config);

        var snap1 = limiter.GetState(subjects, config);
        Thread.Sleep(1100);
        var snap2 = limiter.GetState(subjects, config);

        // Tokens shouldn't appear to change just because we asked twice.
        Assert.Equal(snap1.TokensRemaining, snap2.TokensRemaining);
    }

    [Fact]
    public void Empty_subjects_always_pass()
    {
        // A rule with no subjects can never bind a bucket; the limiter must
        // pass through rather than block. Useful for "global" rules that just
        // dispatch a constant action without a budget.
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var config = new RateLimitConfig(RequestsPerMinute: 1, BurstSize: 1);

        Assert.True(limiter.TryAcquire(Array.Empty<RateLimitSubject>(), config));
        Assert.True(limiter.TryAcquire(Array.Empty<RateLimitSubject>(), config));
    }

    [Fact]
    public void EvictIdle_removes_buckets_older_than_threshold()
    {
        var store = new MemoryRateLimitStateStore();
        var subjects = new[] { new RateLimitSubject(RateLimitSubjectKind.Country, "ZZ") };

        store.TryAcquire(BuildKey(subjects), capacity: 2, tokensPerSecond: 0.5, tokens: 1);
        Assert.NotNull(store.Peek(BuildKey(subjects)));

        // Threshold of 0 ticks evicts everything older than now (i.e. everything).
        var evicted = store.EvictIdle(TimeSpan.FromMilliseconds(-1));
        Assert.True(evicted >= 1);
        Assert.Null(store.Peek(BuildKey(subjects)));
    }

    private static string BuildKey(IReadOnlyList<RateLimitSubject> subjects) =>
        subjects.Count == 1 ? subjects[0].ToString()
            : string.Join("|", subjects.OrderBy(s => s.Kind).ThenBy(s => s.Value).Select(s => s.ToString()));
}
