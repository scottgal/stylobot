using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class InProcessVerdictCacheTests
{
    [Fact]
    public void Get_OnMissingKey_ReturnsNull()
    {
        var cache = new InProcessVerdictCache();

        Assert.Null(cache.TryGet("sig-does-not-exist"));
    }

    [Fact]
    public void PutThenGet_ReturnsTheStoredVerdict()
    {
        var cache = new InProcessVerdictCache(maxEntries: 16, ttl: TimeSpan.FromMinutes(1));
        var verdict = Verdict("sig-1", probability: 0.8, confidence: 0.9);

        cache.Put("sig-1", verdict);

        var got = cache.TryGet("sig-1");
        Assert.NotNull(got);
        Assert.Equal(0.8, got.BotProbability);
        Assert.Equal(0.9, got.Confidence);
    }

    [Fact]
    public void Put_OverwritesExistingEntry()
    {
        var cache = new InProcessVerdictCache();
        cache.Put("sig-1", Verdict("sig-1", probability: 0.2));
        cache.Put("sig-1", Verdict("sig-1", probability: 0.95));

        Assert.Equal(0.95, cache.TryGet("sig-1")!.BotProbability);
    }

    [Fact]
    public void Get_OnExpiredEntry_ReturnsNullAndEvicts()
    {
        var cache = new InProcessVerdictCache(maxEntries: 16, ttl: TimeSpan.FromMilliseconds(1));
        cache.Put("sig-1", Verdict("sig-1", probability: 0.5));

        // Spin briefly past the TTL boundary; 50ms is generous against the 1ms TTL.
        Thread.Sleep(50);

        Assert.Null(cache.TryGet("sig-1"));
        // Eviction is lazy on read; the second call confirms the entry was removed.
        Assert.Null(cache.TryGet("sig-1"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        var cache = new InProcessVerdictCache();
        cache.Put("sig-1", Verdict("sig-1", probability: 0.5));

        cache.Invalidate("sig-1");

        Assert.Null(cache.TryGet("sig-1"));
    }

    [Fact]
    public void Put_BeyondMaxEntries_PrunesOldest()
    {
        // Bug O regression: under a long-tail of fresh primarySigs the cache
        // would grow unbounded without the soft-cap prune; this exercises the
        // overflow branch and asserts the soft-cap is respected.
        const int cap = 8;
        var cache = new InProcessVerdictCache(maxEntries: cap, ttl: TimeSpan.FromMinutes(5));

        for (var i = 0; i < cap * 2; i++)
            cache.Put($"sig-{i}", Verdict($"sig-{i}", probability: 0.5));

        // After overflow, the count should be at or under the cap (eviction
        // runs in batches; the snapshot prune may leave the cache equal to
        // the cap rather than strictly under it).
        Assert.True(cache.Count <= cap, $"cache count {cache.Count} exceeds cap {cap}");
        // The newest entry must still be present -- prune evicts oldest, not newest.
        Assert.NotNull(cache.TryGet($"sig-{cap * 2 - 1}"));
    }

    [Fact]
    public void Get_OnNullOrEmpty_ReturnsNull()
    {
        var cache = new InProcessVerdictCache();
        Assert.Null(cache.TryGet(""));
        Assert.Null(cache.TryGet(null!));
    }

    [Fact]
    public void Put_OnNullOrEmptyKey_DoesNothing()
    {
        var cache = new InProcessVerdictCache();
        cache.Put("", Verdict("x", 0.5));
        cache.Put(null!, Verdict("x", 0.5));

        Assert.Equal(0, cache.Count);
    }

    private static SignatureVerdict Verdict(string sig, double probability, double confidence = 1.0)
        => new()
        {
            SignatureId = sig,
            BotProbability = probability,
            Confidence = confidence,
            RiskBand = RiskBand.Low,
            ThreatScore = 0.0,
            RequestCount = 1,
            LastSeenUtc = DateTime.UtcNow
        };
}