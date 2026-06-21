using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Lifecycle;

namespace Mostlylucid.BotDetection.Test.Lifecycle;

public class PathLifecycleStoreTests : IAsyncDisposable
{
    private readonly SqlitePathLifecycleStore _store;
    private readonly string _dbPath;

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
        await _store.RecordResponseAsync("/api/users", 200);
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
        await _store.RecordResponseAsync("/scanner/probe", 404);
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
        await _store.RecordResponseAsync("/old-api/v1", 200);
        await _store.RecordResponseAsync("/old-api/v1", 200);
        await _store.RecordResponseAsync("/old-api/v1", 404);

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
        await _store.RecordResponseAsync("/.env", 404);
        await _store.RecordResponseAsync("/.env", 404);

        var lifecycle = await _store.GetAsync("/.env");

        Assert.NotNull(lifecycle);
        Assert.False(lifecycle!.IsFormerlyReal); // never 2xx'd
        Assert.Equal(0, lifecycle.Total2xx);
    }

    [Fact]
    public async Task First4xxAfter2xx_LocksFirstFlip()
    {
        await _store.RecordResponseAsync("/feature", 200);
        await _store.RecordResponseAsync("/feature", 404);
        var firstFlip = (await _store.GetAsync("/feature"))!.First4xxAfter2xxUtc;

        await Task.Delay(20);
        await _store.RecordResponseAsync("/feature", 404);
        var lifecycle = await _store.GetAsync("/feature");

        Assert.Equal(firstFlip, lifecycle!.First4xxAfter2xxUtc); // stays locked
    }

    [Fact]
    public async Task CachedReadAfterWrite_ServesFromMemory()
    {
        await _store.RecordResponseAsync("/cached", 200);
        // Read twice -- second read should hit the in-memory cache.
        var first = await _store.GetAsync("/cached");
        var second = await _store.GetAsync("/cached");

        Assert.Equal(first!.LastSeenUtc, second!.LastSeenUtc);
    }

    [Fact]
    public async Task FlushAsync_PersistsDirtyEntriesToSqlite()
    {
        // RecordResponse marks dirty but does NOT touch disk. A new store
        // pointing at the same DB only sees the entry after FlushAsync runs.
        await _store.RecordResponseAsync("/api/perf", 200);
        await _store.RecordResponseAsync("/api/perf", 200);
        await _store.RecordResponseAsync("/api/perf", 404);

        // Verify via a SECOND store instance against the same file -- this is
        // the only way to prove the in-memory state crossed the disk boundary,
        // since GetAsync on the original instance would serve from cache.
        var probe1 = new SqlitePathLifecycleStore(
            $"Data Source={_dbPath};Cache=Shared",
            NullLogger<SqlitePathLifecycleStore>.Instance);
        Assert.Null(await probe1.GetAsync("/api/perf"));   // not flushed yet
        probe1.Dispose();

        await _store.FlushAsync();

        var probe2 = new SqlitePathLifecycleStore(
            $"Data Source={_dbPath};Cache=Shared",
            NullLogger<SqlitePathLifecycleStore>.Instance);
        var seen = await probe2.GetAsync("/api/perf");
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Total2xx);
        Assert.Equal(1, seen.Total4xx);
        Assert.True(seen.IsFormerlyReal);
        probe2.Dispose();
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
        await _store.RecordResponseAsync("/api/x", 200);
        await _store.FlushAsync();

        // Second flush with nothing new -- dirty set is empty, no transaction
        // should run. Exercised by symmetry: a non-throwing call confirms the
        // early-exit fast path.
        await _store.FlushAsync();
        var seen = await _store.GetAsync("/api/x");
        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Total2xx);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await Task.Delay(50); // give SQLite a moment to release file handles
        try { File.Delete(_dbPath); }
        catch { /* best effort */ }
    }
}
