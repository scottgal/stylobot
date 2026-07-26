using FluentAssertions;
using Mostlylucid.BotDetection.Orchestration.Atoms;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Regression coverage for the unbounded-growth OOM in
///     <see cref="FingerprintDimSnapshotCache"/>. The cache was a raw static
///     <c>ConcurrentDictionary</c> with only lazy-per-key TTL eviction inside
///     <c>Get</c>; under identity rotation (one-shot / rotated fingerprintIds
///     that are never read back before their 24h TTL) it grew for the whole
///     pod lifetime and OOM-crashed the gateway roughly every 1-2h. It is now
///     backed by the house <see cref="Mostlylucid.BotDetection.Services.BoundedCache{TKey,TValue}"/>
///     which actively size-caps on every <c>Set</c>.
/// </summary>
public sealed class FingerprintDimSnapshotCacheBoundingTests
{
    private static FingerprintDimSnapshotCache.DimSnapshot Snap(DateTimeOffset? lastSeen = null) =>
        new(
            Country: "US",
            Asn: "AS15169",
            UaFamily: "Chrome",
            IsDatacenter: false,
            IsTorOrVpn: false,
            LastSeenUtc: lastSeen ?? DateTimeOffset.UtcNow);

    [Fact]
    public void Set_is_bounded_under_high_cardinality_churn()
    {
        // maxSize deliberately small; insert two orders of magnitude more
        // DISTINCT keys (the rotated-fingerprint flood). On the old unbounded
        // dict Count would be 10_000; bounded it must never exceed maxSize.
        const int maxSize = 100;
        var cache = new FingerprintDimSnapshotCache(maxSize: maxSize, ttl: TimeSpan.FromHours(24));

        for (var i = 0; i < 10_000; i++)
            cache.Set($"fp-{i}", Snap());

        cache.Count.Should().BeLessThanOrEqualTo(maxSize,
            "the cache must actively evict so a rotated-fingerprint flood cannot exhaust the heap");
    }

    [Fact]
    public void Get_returns_snapshot_within_ttl()
    {
        var cache = new FingerprintDimSnapshotCache(maxSize: 100, ttl: TimeSpan.FromHours(24));
        var snap = Snap();

        cache.Set("fp-1", snap);

        cache.Get("fp-1").Should().Be(snap);
    }

    [Fact]
    public void Get_returns_null_when_absent()
    {
        var cache = new FingerprintDimSnapshotCache(maxSize: 100, ttl: TimeSpan.FromHours(24));

        cache.Get("missing").Should().BeNull();
    }

    [Fact]
    public void Get_returns_null_after_ttl_expiry()
    {
        // Sub-millisecond TTL so the entry is stale by the time we read it.
        var cache = new FingerprintDimSnapshotCache(maxSize: 100, ttl: TimeSpan.FromMilliseconds(1));
        cache.Set("fp-1", Snap());

        Thread.Sleep(20);

        cache.Get("fp-1").Should().BeNull("the snapshot has aged past its TTL");
    }

    [Fact]
    public void Set_overwrites_same_key()
    {
        var cache = new FingerprintDimSnapshotCache(maxSize: 100, ttl: TimeSpan.FromHours(24));
        var first = Snap() with { Country = "US" };
        var second = Snap() with { Country = "GB" };

        cache.Set("fp-1", first);
        cache.Set("fp-1", second);

        cache.Get("fp-1").Should().Be(second);
        cache.Count.Should().Be(1, "overwriting a key must not grow the cache");
    }

    [Fact]
    public void Reset_clears_all_entries()
    {
        var cache = new FingerprintDimSnapshotCache(maxSize: 100, ttl: TimeSpan.FromHours(24));
        for (var i = 0; i < 50; i++)
            cache.Set($"fp-{i}", Snap());

        cache.Reset();

        cache.Count.Should().Be(0);
        cache.Get("fp-0").Should().BeNull();
    }
}
