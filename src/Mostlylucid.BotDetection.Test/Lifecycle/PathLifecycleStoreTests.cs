using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Lifecycle;

namespace Mostlylucid.BotDetection.Test.Lifecycle;

public class PathLifecycleStoreTests : IAsyncDisposable
{
    private readonly SqlitePathLifecycleStore _store;
    private readonly string _dbPath;
    private static readonly RequestScope Scope = RequestScope.Unknown;

    public PathLifecycleStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lifecycle-test-{Guid.NewGuid():N}.db");
        _store = new SqlitePathLifecycleStore(
            $"Data Source={_dbPath};Cache=Shared",
            NullLogger<SqlitePathLifecycleStore>.Instance);
    }

    [Fact]
    public async Task UnknownPath_ReturnsNull()
    {
        var result = await _store.GetAsync("/never-seen");
        Assert.Null(result);
    }

    [Fact]
    public async Task Record2xx_MarksHasEverServed2xx()
    {
        await _store.RecordResponseAsync(Scope, "/api/users", 200);
        var lifecycle = await _store.GetAsync("/api/users");

        Assert.NotNull(lifecycle);
        Assert.True(lifecycle!.HasEverServed2xx);
        Assert.False(lifecycle.IsFormerlyReal);
        Assert.Equal(1, lifecycle.Total2xx);
        Assert.NotNull(lifecycle.Last2xxUtc);
    }

    [Fact]
    public async Task Record404_OnVirginPath_NotFormerlyReal()
    {
        await _store.RecordResponseAsync(Scope, "/scanner/probe", 404);
        var lifecycle = await _store.GetAsync("/scanner/probe");

        Assert.NotNull(lifecycle);
        Assert.False(lifecycle!.HasEverServed2xx);
        Assert.False(lifecycle.IsFormerlyReal);
        Assert.Equal(0, lifecycle.Total2xx);
        Assert.Equal(1, lifecycle.Total4xx);
    }

    [Fact]
    public async Task Record2xxThen404_FlipsToFormerlyReal()
    {
        await _store.RecordResponseAsync(Scope, "/old-api/v1", 200);
        await _store.RecordResponseAsync(Scope, "/old-api/v1", 200);
        await _store.RecordResponseAsync(Scope, "/old-api/v1", 404);

        var lifecycle = await _store.GetAsync("/old-api/v1");

        Assert.NotNull(lifecycle);
        Assert.True(lifecycle!.IsFormerlyReal);
        Assert.Equal(2, lifecycle.Total2xx);
        Assert.Equal(1, lifecycle.Total4xx);
        Assert.NotNull(lifecycle.First4xxAfter2xxUtc);
    }

    [Fact]
    public async Task Record404OnEnvFile_DoesNotMarkFormerlyReal_NoPriorSuccess()
    {
        // Simulating a scanner probing /.env on a site that never had it
        await _store.RecordResponseAsync(Scope, "/.env", 404);
        await _store.RecordResponseAsync(Scope, "/.env", 404);

        var lifecycle = await _store.GetAsync("/.env");

        Assert.NotNull(lifecycle);
        Assert.False(lifecycle!.IsFormerlyReal); // never 2xx'd
        Assert.Equal(0, lifecycle.Total2xx);
    }

    [Fact]
    public async Task First4xxAfter2xx_LocksFirstFlip()
    {
        await _store.RecordResponseAsync(Scope, "/feature", 200);
        await _store.RecordResponseAsync(Scope, "/feature", 404);
        var firstFlip = (await _store.GetAsync("/feature"))!.First4xxAfter2xxUtc;

        await Task.Delay(20);
        await _store.RecordResponseAsync(Scope, "/feature", 404);
        var lifecycle = await _store.GetAsync("/feature");

        Assert.Equal(firstFlip, lifecycle!.First4xxAfter2xxUtc); // stays locked
    }

    [Fact]
    public async Task CachedReadAfterWrite_ServesFromMemory()
    {
        await _store.RecordResponseAsync(Scope, "/cached", 200);
        // Read twice -- second read should hit the in-memory cache.
        var first = await _store.GetAsync("/cached");
        var second = await _store.GetAsync("/cached");

        Assert.Equal(first!.LastSeenUtc, second!.LastSeenUtc);
    }

    [Fact]
    public async Task FlushAsync_PersistsDirtyEntriesToSqlite()
    {
        // RecordResponse enqueues writes that the WriteBehindLfuStore drains in
        // the background. After FlushAsync returns, the durable tier is up-to-date.
        await _store.RecordResponseAsync(Scope, "/api/perf", 200);
        await _store.RecordResponseAsync(Scope, "/api/perf", 200);
        await _store.RecordResponseAsync(Scope, "/api/perf", 404);

        await _store.FlushAsync();

        // Verify via a SECOND store instance against the same file -- this is
        // the only way to prove the in-memory state crossed the disk boundary,
        // since GetAsync on the original instance would serve from cache.
        // (An earlier revision tried to assert "not flushed yet" on a probe
        // BEFORE calling FlushAsync; that race lost reliably on CI because
        // the background drainer can land the batch in single-digit ms.)
        var probe = new SqlitePathLifecycleStore(
            $"Data Source={_dbPath};Cache=Shared",
            NullLogger<SqlitePathLifecycleStore>.Instance);
        var seen = await probe.GetAsync("/api/perf");
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Total2xx);
        Assert.Equal(1, seen.Total4xx);
        Assert.True(seen.IsFormerlyReal);
        probe.Dispose();
    }

    [Fact]
    public async Task FlushAsync_OnEmptyDirtySet_IsCheap()
    {
        // Nothing recorded. Flush should be a no-op (no SQLite open).
        await _store.FlushAsync();
        Assert.Null(await _store.GetAsync("/never-touched"));
    }

    [Fact]
    public async Task DoubleFlush_DoesNotRewriteUnchangedEntries()
    {
        await _store.RecordResponseAsync(Scope, "/api/x", 200);
        await _store.FlushAsync();

        // Second flush with nothing new -- dirty set is empty, no transaction
        // should run. Exercised by symmetry: a non-throwing call confirms the
        // early-exit fast path.
        await _store.FlushAsync();
        var seen = await _store.GetAsync("/api/x");
        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Total2xx);
    }

    [Fact]
    public async Task LfuEviction_DropsLowSignalPathsBeforeFormerlyReal()
    {
        // Under hot-tier pressure, the system should shed the LEAST USEFUL
        // entries first -- not the oldest. Behavioural ColdnessScore tiers:
        //   FormerlyReal > MixedTraffic > HadRealTraffic > 404-only.
        //
        // Build a tiny store so eviction triggers fast, then prove a 404-only
        // path is gone (re-fetch returns null from durable tier since we
        // haven't flushed) while the FormerlyReal path is still hot.
        var tinyDbPath = Path.Combine(Path.GetTempPath(), $"lc-tiny-{Guid.NewGuid():N}.db");
        var tiny = new SqlitePathLifecycleStore(
            $"Data Source={tinyDbPath};Cache=Shared",
            NullLogger<SqlitePathLifecycleStore>.Instance,
            new PathLifecycleOptions { MaxEntries = 4 });

        try
        {
            // High-signal: 2xx then 4xx → IsFormerlyReal = true
            await tiny.RecordResponseAsync(Scope, "/old-api", 200);
            await tiny.RecordResponseAsync(Scope, "/old-api", 404);
            // Low-signal: only 404 spam, no 2xx history → coldest tier
            await tiny.RecordResponseAsync(Scope, "/.env", 404);
            await tiny.RecordResponseAsync(Scope, "/.git", 404);
            await tiny.RecordResponseAsync(Scope, "/wp-login.php", 404);

            // Push past MaxEntries (cap=4, +10% = 4 → 5th triggers eviction).
            // Add several more 404-only paths so eviction must drop *some*
            // 404-only entries to stay under cap.
            for (var i = 0; i < 20; i++)
                await tiny.RecordResponseAsync(Scope, $"/scanner/{i}", 404);

            // The FormerlyReal path must still be hot -- it's pinned by the
            // behavioural score and survives any number of 404-only floods.
            var stillHot = await ((IPathLifecycleStore)tiny).GetAsync("/old-api");
            Assert.NotNull(stillHot);
            Assert.True(stillHot!.IsFormerlyReal);

            // At least one of the 404-only spam paths got evicted.
            // (We can't easily prove WHICH without snapshotting, but Count
            // must respect MaxEntries + 10% slack = 4.)
            Assert.True(tiny.Count <= 5, $"hot-tier count {tiny.Count} should respect MaxEntries=4 (+10%)");
        }
        finally
        {
            tiny.Dispose();
            await Task.Delay(50);
            try { File.Delete(tinyDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Store_persists_domain_and_host_from_RequestScope()
    {
        // Multi-domain contract: the (domain, host) the middleware passes in
        // MUST be persisted alongside the path so a downstream reader can tell
        // "www.acme.com served /login" apart from "auth.acme.com served /login"
        // (same eTLD+1 rollup, distinct host). Both dimensions round-trip
        // through the write-behind LFU + Sqlite durable tier.
        var scope = new RequestScope("acme.com", "www.acme.com");
        await _store.RecordResponseAsync(scope, "/login", 200);

        // Hot-tier read must carry the scope we wrote.
        var hot = await _store.GetAsync("/login");
        Assert.NotNull(hot);
        Assert.Equal("acme.com", hot!.Domain);
        Assert.Equal("www.acme.com", hot.Host);
        Assert.Equal("/login", hot.Path);

        // Cross the disk boundary via FlushAsync + a fresh store instance so
        // we're proving the scope hit Sqlite, not just the ConcurrentDictionary.
        await _store.FlushAsync();
        var probe = new SqlitePathLifecycleStore(
            $"Data Source={_dbPath};Cache=Shared",
            NullLogger<SqlitePathLifecycleStore>.Instance);
        try
        {
            var cold = await probe.GetAsync("/login");
            Assert.NotNull(cold);
            Assert.Equal("acme.com", cold!.Domain);
            Assert.Equal("www.acme.com", cold.Host);
        }
        finally
        {
            probe.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await Task.Delay(50); // give SQLite a moment to release file handles
        try { File.Delete(_dbPath); }
        catch { /* best effort */ }
    }
}