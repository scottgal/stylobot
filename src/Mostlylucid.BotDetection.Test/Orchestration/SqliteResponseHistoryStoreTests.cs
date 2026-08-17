using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins <see cref="SqliteResponseHistoryStore"/>'s durability contract -- the fix for the
///     CLAUDE.md violation surfaced by code review of the P0 ResponseCoordinator wiring (2026-08-17):
///     "NEVER use in-memory stores for persistence... for anything that matters." Before this store
///     existed, ResponseCoordinator's per-client history was ClientResponseTrackingAtom's bounded,
///     TTL-evicted, in-process-only ring buffer -- a restart silently reset a scanning client's
///     history to zero.
/// </summary>
public class SqliteResponseHistoryStoreTests : IAsyncDisposable
{
    private readonly SqliteResponseHistoryStore _store;
    private readonly string _dbPath;

    public SqliteResponseHistoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"response-history-test-{Guid.NewGuid():N}.db");
        _store = new SqliteResponseHistoryStore(
            NullLogger<SqliteResponseHistoryStore>.Instance,
            $"Data Source={_dbPath};Cache=Shared");
    }

    [Fact]
    public async Task UnknownClient_ReturnsNull()
    {
        var result = await _store.GetAsync("never-seen");
        Assert.Null(result);
    }

    [Fact]
    public void Record_AggregatesCountsByStatusCodeBucket()
    {
        _store.Record("client-1", new ResponseHistoryOp("client-1", 200, "/", false, DateTimeOffset.UtcNow));
        _store.Record("client-1", new ResponseHistoryOp("client-1", 404, "/a", false, DateTimeOffset.UtcNow));
        var value = _store.Record("client-1", new ResponseHistoryOp("client-1", 404, "/b", false, DateTimeOffset.UtcNow));

        Assert.Equal(3, value.TotalCount);
        Assert.Equal(1, value.Count2xx);
        Assert.Equal(2, value.Count404);
        Assert.Equal(2, value.UniqueNotFoundPathCount);
    }

    [Fact]
    public void Record_SamePathTwice_CountsOnceForUniqueness()
    {
        _store.Record("client-2", new ResponseHistoryOp("client-2", 404, "/repeat", false, DateTimeOffset.UtcNow));
        var value = _store.Record("client-2", new ResponseHistoryOp("client-2", 404, "/repeat", false, DateTimeOffset.UtcNow));

        Assert.Equal(2, value.Count404);
        // The same path hit twice is one unique path, two total 404s.
        Assert.Equal(1, value.UniqueNotFoundPathCount);
    }

    [Fact]
    public void Record_HoneypotAndAuthFailureFlags_TrackSeparately()
    {
        _store.Record("client-3", new ResponseHistoryOp("client-3", 401, "/login", false, DateTimeOffset.UtcNow));
        var value = _store.Record("client-3", new ResponseHistoryOp("client-3", 200, "/wp-admin", true, DateTimeOffset.UtcNow));

        Assert.Equal(1, value.AuthFailures);
        Assert.Equal(1, value.HoneypotHits);
    }

    [Fact]
    public async Task FlushAsync_ThenFreshStoreInstance_ReadsPersistedAggregate()
    {
        _store.Record("client-4", new ResponseHistoryOp("client-4", 404, "/x", false, DateTimeOffset.UtcNow));
        _store.Record("client-4", new ResponseHistoryOp("client-4", 404, "/y", false, DateTimeOffset.UtcNow));
        _store.Record("client-4", new ResponseHistoryOp("client-4", 404, "/z", false, DateTimeOffset.UtcNow));
        await _store.FlushAsync();

        // Cross the disk boundary via a fresh store instance pointed at the same file --
        // simulates a process restart. The original store's hot tier is irrelevant here.
        var probe = new SqliteResponseHistoryStore(
            NullLogger<SqliteResponseHistoryStore>.Instance,
            $"Data Source={_dbPath};Cache=Shared");
        try
        {
            var cold = await probe.GetAsync("client-4");
            Assert.NotNull(cold);
            Assert.Equal(3, cold!.Count404);
            Assert.Equal(3, cold.UniqueNotFoundPathCount);
            // Cold load: no path set survives, only the count -- see ResponseHistoryAggregate's
            // remarks. NotFoundPaths is empty; SeededUniqueNotFoundPathsBaseline carries the count.
            Assert.Empty(cold.NotFoundPaths);
            Assert.Equal(3, cold.SeededUniqueNotFoundPathsBaseline);
        }
        finally
        {
            probe.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        // Give SQLite a moment to release file handles before cleanup, matching
        // PathLifecycleStoreTests' established pattern for this test shape.
        await Task.Delay(50);
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}
