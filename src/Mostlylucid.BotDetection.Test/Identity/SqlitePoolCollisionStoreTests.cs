using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Hot-tier behaviour tests for <see cref="SqlitePoolCollisionStore"/>.
///     The store is a <c>WriteBehindLfuStore</c> subclass; these cases cover
///     the per-request hot-path semantics (observe, dedup, window) using the
///     in-memory ConcurrentDictionary tier. The cold-load + drainer flush
///     paths are covered by <see cref="SqlitePoolCollisionStoreColdLoadTests"/>.
/// </summary>
public class SqlitePoolCollisionStoreTests : IAsyncLifetime
{
    private SqlitePoolCollisionStore _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"pool-coll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "botdetection.db")
        });
        _store = new SqlitePoolCollisionStore(opts, NullLogger<SqlitePoolCollisionStore>.Instance);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void DistinctContextsInWindow_SameShapeDifferentContexts_CountsEachOnce()
    {
        // Multilogin pattern: same fingerprint profile distributed to three
        // different customers, each on a different residential IP and session.
        var now = DateTimeOffset.UtcNow;
        _store.Observe("shape-A", "ip-1", "sess-1", now);
        _store.Observe("shape-A", "ip-2", "sess-2", now);
        _store.Observe("shape-A", "ip-3", "sess-3", now);

        Assert.Equal(3, _store.DistinctContextsInWindow("shape-A", now.AddHours(-1)));
    }

    [Fact]
    public void DistinctContextsInWindow_SameContextRepeated_DedupsToOne()
    {
        // Legitimate user refreshing the page. Same IP + session = one context.
        var now = DateTimeOffset.UtcNow;
        _store.Observe("shape-A", "ip-1", "sess-1", now);
        _store.Observe("shape-A", "ip-1", "sess-1", now.AddMinutes(2));
        _store.Observe("shape-A", "ip-1", "sess-1", now.AddMinutes(5));

        Assert.Equal(1, _store.DistinctContextsInWindow("shape-A", now.AddHours(-1)));
    }

    [Fact]
    public void DistinctContextsInWindow_OutsideWindow_ExcludedFromCount()
    {
        // The window is sliding; observations older than `since` are out of scope.
        var earlier = DateTimeOffset.UtcNow.AddDays(-1);
        _store.Observe("shape-A", "ip-1", "sess-1", earlier);
        _store.Observe("shape-A", "ip-2", "sess-2", earlier);
        _store.Observe("shape-A", "ip-3", "sess-3", earlier);

        Assert.Equal(0, _store.DistinctContextsInWindow("shape-A", DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public void DistinctContextsInWindow_UnknownShape_ReturnsZero()
    {
        Assert.Equal(0, _store.DistinctContextsInWindow("never-seen", DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public void Observe_EmptyShape_NoOp()
    {
        _store.Observe("", "ip-1", "sess-1", DateTimeOffset.UtcNow);
        Assert.Equal(0, _store.DistinctContextsInWindow("", DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(0, _store.TrackedShapeCount);
    }

    [Fact]
    public void TrackedShapeCount_GrowsWithDistinctShapes()
    {
        var now = DateTimeOffset.UtcNow;
        _store.Observe("shape-A", "ip-1", "sess-1", now);
        _store.Observe("shape-B", "ip-1", "sess-1", now);
        _store.Observe("shape-C", "ip-1", "sess-1", now);
        _store.Observe("shape-A", "ip-2", "sess-2", now); // existing shape, new context

        Assert.Equal(3, _store.TrackedShapeCount);
    }
}

/// <summary>
///     Cold-load and durable-tier tests for <see cref="SqlitePoolCollisionStore"/>.
///     Exercises the SQLite backing: observations land in the durable tier via
///     the drainer; a fresh store with the same DB sees them via
///     <see cref="Mostlylucid.BotDetection.Storage.WriteBehindLfuStore{TKey, TValue, TWriteOp}.GetAsync"/>.
/// </summary>
public class SqlitePoolCollisionStoreColdLoadTests : IAsyncLifetime
{
    private string _dbDir = null!;
    private BotDetectionOptions _opts = null!;

    public Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"pool-coll-cold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        _opts = new BotDetectionOptions { DatabasePath = Path.Combine(_dbDir, "botdetection.db") };
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ColdLoad_AfterRestart_RehydratesRecentObservations()
    {
        // First instance: observe three contexts, give the drainer a beat to
        // flush, then dispose to terminate the drainer cleanly.
        var optsWrapper = Options.Create(_opts);
        var now = DateTimeOffset.UtcNow;
        var first = new SqlitePoolCollisionStore(optsWrapper, NullLogger<SqlitePoolCollisionStore>.Instance);
        try
        {
            await first.InitializeAsync();
            first.Observe("shape-A", "ip-1", "sess-1", now);
            first.Observe("shape-A", "ip-2", "sess-2", now);
            first.Observe("shape-A", "ip-3", "sess-3", now);
            // Default drain interval is 500ms inside the base class; wait
            // long enough for the batch to flush.
            await Task.Delay(900);
        }
        finally
        {
            first.Dispose();
        }

        // Second instance: same DB file, fresh hot tier. Cold load via
        // GetAsync surfaces the persisted observations.
        var second = new SqlitePoolCollisionStore(optsWrapper, NullLogger<SqlitePoolCollisionStore>.Instance);
        try
        {
            await second.InitializeAsync();
            var bucket = await second.GetAsync("shape-A");
            Assert.NotNull(bucket);
            Assert.Equal(3, bucket!.DistinctContextsSince(now.AddHours(-1)));
        }
        finally
        {
            second.Dispose();
        }
    }
}
