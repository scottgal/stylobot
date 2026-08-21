using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Regression tests for the gateway-OOM leak class: several singleton dictionaries
///     were keyed by high-cardinality per-request keys (primarySignature / uaFamily /
///     request path) with no cap and no eviction, so they grew forever under
///     rotating-fingerprint traffic. Each test floods a store with 100k distinct keys
///     and asserts the resident entry count stays at/under the store's cap, plus that
///     the store's normal read/decay behaviour still works.
/// </summary>
public class UnboundedStoreCapTests
{
    private const int Flood = 100_000;

    // 1. UaProfileStore._signatureProfiles (cap 10_000)
    [Fact]
    public void UaProfileStore_signatureProfiles_bounded_and_readable()
    {
        var store = new UaProfileStore(NullLogger<UaProfileStore>.Instance);

        for (var i = 0; i < Flood; i++)
            store.RecordSignature($"sig-{i}", "chrome", "browser", 0.9);

        Assert.True(store.SignatureProfileCount <= 10_000,
            $"expected <= 10_000, got {store.SignatureProfileCount}");

        // Behaviour preserved: a freshly written value is readable.
        store.RecordSignature("sig-hot", "firefox", "browser", 0.42);
        var (family, tier, score) = store.GetSignatureProfile("sig-hot");
        Assert.Equal("firefox", family);
        Assert.Equal("browser", tier);
        Assert.Equal(0.42, score, 3);
    }

    // 2. InMemoryPatternReputationCache._cache (hard cap 50_000, independent of 90-day GC)
    [Fact]
    public void PatternReputationCache_bounded_and_readable()
    {
        var updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance,
            Options.Create(new BotDetectionOptions()));
        var cache = new InMemoryPatternReputationCache(
            NullLogger<InMemoryPatternReputationCache>.Instance, updater);

        for (var i = 0; i < Flood; i++)
            cache.GetOrCreate($"ua:sig-{i}", "ua", $"sig-{i}");

        Assert.True(cache.ResidentPatternCount <= 50_000,
            $"expected <= 50_000, got {cache.ResidentPatternCount}");

        // Behaviour preserved: GetOrCreate returns a live, updatable reputation.
        var rep = cache.GetOrCreate("ua:hot", "ua", "hot");
        Assert.Equal("ua:hot", rep.PatternId);
        Assert.Equal(ReputationState.Neutral, rep.State);
        Assert.NotNull(cache.Get("ua:hot"));
    }

    // 3. DeploymentNormTracker._buckets (cap 20_000, Halve() decay preserved)
    [Fact]
    public void DeploymentNormTracker_bounded_and_decay_preserved()
    {
        var tracker = new DeploymentNormTracker(windowSize: 500, warmupRequests: 0);

        for (var i = 0; i < Flood; i++)
            tracker.Record("http2", $"botfamily-{i}", present: true);

        Assert.True(tracker.BucketCount <= 20_000,
            $"expected <= 20_000, got {tracker.BucketCount}");

        // Behaviour preserved: rate accounting still works for a surviving bucket.
        tracker.Record("http2", "hotfamily", present: true);
        tracker.Record("http2", "hotfamily", present: true);
        tracker.Record("http2", "hotfamily", present: false);
        var (rate, samples) = tracker.GetRate("http2", "hotfamily");
        Assert.Equal(3, samples);
        Assert.Equal(2.0 / 3.0, rate, 3);
    }

    // 4. FingerprintPopulationTracker._buckets (cap 20_000, Halve() decay preserved)
    [Fact]
    public void FingerprintPopulationTracker_bounded_and_decay_preserved()
    {
        var tracker = new FingerprintPopulationTracker(windowSize: 500);

        for (var i = 0; i < Flood; i++)
            tracker.Record($"botfamily-{i}", "document", hasFingerprint: true);

        Assert.True(tracker.BucketCount <= 20_000,
            $"expected <= 20_000, got {tracker.BucketCount}");

        // Behaviour preserved.
        tracker.Record("hotfamily", "document", hasFingerprint: true);
        tracker.Record("hotfamily", "document", hasFingerprint: false);
        var (rate, samples) = tracker.GetRate("hotfamily", "document");
        Assert.Equal(2, samples);
        Assert.Equal(0.5, rate, 3);
    }

    // 5. EndpointDivergenceTracker._windows (cap 10_000)
    [Fact]
    public void EndpointDivergenceTracker_bounded_and_readable()
    {
        var tracker = new EndpointDivergenceTracker();

        for (var i = 0; i < Flood; i++)
            tracker.RecordSession($"/path/{i}");

        Assert.True(tracker.WindowCount <= 10_000,
            $"expected <= 10_000, got {tracker.WindowCount}");

        // Behaviour preserved: a freshly recorded path tracks its own stats.
        tracker.RecordSession("/hot");
        tracker.RecordDivergence("/hot");
        var stats = tracker.GetStats("/hot");
        Assert.Equal(1, stats.TotalSessions);
        Assert.Equal(1, stats.DivergenceCount);
    }

    // 6. CentroidSequenceStore._staleEndpoints (cap 10_000)
    [Fact]
    public void CentroidSequenceStore_staleEndpoints_bounded_and_readable()
    {
        var store = new CentroidSequenceStore(
            () => new SqliteConnection("Data Source=:memory:"),
            NullLogger<CentroidSequenceStore>.Instance);

        for (var i = 0; i < Flood; i++)
            store.MarkEndpointStale($"/content/{i}");

        Assert.True(store.StaleEndpointCount <= 10_000,
            $"expected <= 10_000, got {store.StaleEndpointCount}");

        // Behaviour preserved: a freshly marked path reads back as stale.
        store.MarkEndpointStale("/hot");
        Assert.True(store.IsEndpointStale("/hot"));
        store.ClearEndpointStale("/hot");
        Assert.False(store.IsEndpointStale("/hot"));
    }

    // 7. SessionStore._activeSignatures (Analysis) — shadow index tracks session lifetime.
    [Fact]
    public void SessionStore_activeSignatures_capped_under_flood()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance);

        for (var i = 0; i < Flood + 20_000; i++)
            store.RecordRequestAsync($"sig-{i}",
                new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, "/", 200))
                .GetAwaiter().GetResult();

        // Hard safety cap keeps the shadow index bounded even if callbacks lag.
        Assert.True(store.ActiveSignatureCount <= 100_000,
            $"expected <= 100_000, got {store.ActiveSignatureCount}");
    }

    // 7b. SessionStore._cache (Analysis) — the actual memory-heavy payload (a
    // List<SessionRequest> per signature) must itself be capped in lockstep with
    // ActiveSignaturesMaxEntries. Before this fix, EvictActiveSignaturesIfNeeded only
    // pruned the _activeSignatures shadow index and never touched _cache, so under
    // growing-cardinality one-shot signatures the cache grew without bound (self-healing
    // only via a ~35min sliding TTL, which cannot keep pace with a sustained arrival
    // rate) -- the FOSS-side half of the 2026-08-21 OOM P0.
    [Fact]
    public void SessionStore_cache_bounded_under_growing_cardinality_flood()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance,
            activeSignaturesMaxEntries: 1_000);

        for (var i = 0; i < 5_000; i++)
            store.RecordRequestAsync($"sig-{i}",
                new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, "/", 200))
                .GetAwaiter().GetResult();

        // The real bound is on _cache itself, not just the shadow index -- TTL alone
        // cannot be relied on to reclaim entries fast enough under sustained
        // growing-cardinality traffic within a single run.
        Assert.True(cache.Count <= 1_100,
            $"expected _cache entry count to be capped near ActiveSignaturesMaxEntries (1000), got {cache.Count}");
    }

    [Fact]
    public async Task SessionStore_activeSignatures_removed_when_session_slot_evicted()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance);

        await store.RecordRequestAsync("sig-evict",
            new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, "/", 200));

        Assert.Equal(1, store.ActiveSignatureCount);

        // Simulate the session slot leaving the cache (expiry / capacity eviction).
        cache.Remove("session:current:sig-evict");

        // PostEvictionCallback runs on a background thread; poll for the shadow removal.
        var sw = Stopwatch.StartNew();
        while (store.ActiveSignatureCount != 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);

        Assert.Equal(0, store.ActiveSignatureCount);
    }

    [Fact]
    public async Task SessionStore_activeSignatures_survives_replaced_live_session_slot()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance);

        await store.RecordRequestAsync("sig-replaced",
            new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, "/one", 200));
        await store.RecordRequestAsync("sig-replaced",
            new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, "/two", 200));

        // Replacing the session-cache slot must not let the old callback erase the
        // still-live shutdown projection for this signature.
        await Task.Delay(50);
        Assert.Equal(1, store.ActiveSignatureCount);
    }

    [Fact]
    public void PatternReputationCache_ip_and_ua_identities_remain_isolated()
    {
        var updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance,
            Options.Create(new BotDetectionOptions()));
        var cache = new InMemoryPatternReputationCache(
            NullLogger<InMemoryPatternReputationCache>.Instance, updater);

        var ip = cache.GetOrCreate("ip:192.0.2.1", "ip", "192.0.2.1");
        var ua = cache.GetOrCreate("ua:browser-a", "ua", "browser-a");

        Assert.Equal("ip", ip.PatternType);
        Assert.Equal("ua", ua.PatternType);
        Assert.NotSame(ip, ua);
        Assert.Same(ip, cache.Get("ip:192.0.2.1"));
        Assert.Same(ua, cache.Get("ua:browser-a"));
    }

    [Fact]
    public void EndpointDivergence_and_staleness_are_isolated_by_path()
    {
        var divergence = new EndpointDivergenceTracker();
        divergence.RecordSession("/changed");
        divergence.RecordDivergence("/changed");
        divergence.RecordSession("/stable");

        Assert.Equal(1, divergence.GetStats("/changed").DivergenceCount);
        Assert.Equal(0, divergence.GetStats("/stable").DivergenceCount);

        var stale = new CentroidSequenceStore(
            () => new SqliteConnection("Data Source=:memory:"),
            NullLogger<CentroidSequenceStore>.Instance);
        stale.MarkEndpointStale("/changed");
        Assert.True(stale.IsEndpointStale("/changed"));
        Assert.False(stale.IsEndpointStale("/stable"));
        stale.ClearEndpointStale("/changed");
        Assert.False(stale.IsEndpointStale("/changed"));
    }
}
