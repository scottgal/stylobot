using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Hot-tier + cold-load tests for <see cref="WaveformHistoryStore"/>.
///     Exercises the canonical <c>WriteBehindLfuStore</c> pattern that
///     replaced the previous <c>IMemoryCache</c> backing: hot-tier merges
///     preserve the 30-min sliding window + per-signature cap, and SQLite
///     persistence carries history across store instances.
/// </summary>
public class WaveformHistoryStoreTests : IAsyncLifetime
{
    private WaveformHistoryStore _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"waveform-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "botdetection.db")
        });
        _store = new WaveformHistoryStore(opts, NullLogger<WaveformHistoryStore>.Instance);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static RequestSnapshot Snapshot(DateTimeOffset ts, string path = "/", ContentClass cls = ContentClass.Page) =>
        new()
        {
            Timestamp = ts,
            Path = path,
            Method = "GET",
            StatusCode = 200,
            UserAgent = "Mozilla/5.0",
            RefererHash = "",
            ContentClass = cls
        };

    [Fact]
    public void Record_AppendsToHistory()
    {
        var t0 = DateTimeOffset.UtcNow;
        _store.Record("sig", Snapshot(t0, "/a"));
        _store.Record("sig", Snapshot(t0.AddSeconds(1), "/b"));
        _store.Record("sig", Snapshot(t0.AddSeconds(2), "/c"));

        var history = _store.TryGetHot("sig");
        Assert.NotNull(history);
        Assert.Equal(3, history!.Snapshots.Count);
        Assert.Equal("/a", history.Snapshots[0].Path);
        Assert.Equal("/c", history.Snapshots[^1].Path);
    }

    [Fact]
    public void Record_BeyondWindow_PrunesOldest()
    {
        var now = DateTimeOffset.UtcNow;
        var ancient = now.Subtract(WaveformHistoryStore.HistoryWindow).Subtract(TimeSpan.FromMinutes(5));
        _store.Record("sig", Snapshot(ancient, "/old"));
        _store.Record("sig", Snapshot(now, "/fresh"));

        var history = _store.TryGetHot("sig");
        Assert.NotNull(history);
        Assert.Single(history!.Snapshots);
        Assert.Equal("/fresh", history.Snapshots[0].Path);
    }

    [Fact]
    public void Record_BeyondCap_DropsOldestFifo()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < WaveformHistoryStore.MaxSnapshotsPerSignature + 5; i++)
            _store.Record("sig", Snapshot(now.AddMilliseconds(i), $"/{i}"));

        var history = _store.TryGetHot("sig");
        Assert.NotNull(history);
        Assert.Equal(WaveformHistoryStore.MaxSnapshotsPerSignature, history!.Snapshots.Count);
        // Oldest 5 were trimmed; first surviving entry is /5.
        Assert.Equal("/5", history.Snapshots[0].Path);
    }

    [Fact]
    public void UpdateLastContentClass_RewritesLastSnapshotInPlace()
    {
        var t0 = DateTimeOffset.UtcNow;
        _store.Record("sig", Snapshot(t0, "/a", ContentClass.Page));
        _store.Record("sig", Snapshot(t0.AddSeconds(1), "/b", ContentClass.Asset));

        _store.UpdateLastContentClass("sig", ContentClass.Api);

        var history = _store.TryGetHot("sig");
        Assert.NotNull(history);
        Assert.Equal(2, history!.Snapshots.Count); // Did NOT append a third
        Assert.Equal(ContentClass.Page, history.Snapshots[0].ContentClass);
        Assert.Equal(ContentClass.Api, history.Snapshots[^1].ContentClass);
    }

    [Fact]
    public void UpdateLastContentClass_NoHotEntry_NoOps()
    {
        _store.UpdateLastContentClass("missing", ContentClass.Api);
        // Did not throw, did not create a phantom entry.
        Assert.Null(_store.TryGetHot("missing"));
    }
}

/// <summary>
///     Cold-load round-trip via SQLite. Persistence must survive a store
///     restart with the same DB file.
/// </summary>
public class WaveformHistoryStoreColdLoadTests : IAsyncLifetime
{
    private string _dbDir = null!;
    private BotDetectionOptions _opts = null!;

    public Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"waveform-cold-{Guid.NewGuid():N}");
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
    public async Task ColdLoad_AfterRestart_RehydratesRecentHistory()
    {
        var optsWrapper = Options.Create(_opts);
        var now = DateTimeOffset.UtcNow;

        var first = new WaveformHistoryStore(optsWrapper, NullLogger<WaveformHistoryStore>.Instance);
        try
        {
            await first.InitializeAsync();
            first.Record("sig", new RequestSnapshot
            {
                Timestamp = now, Path = "/a", Method = "GET", StatusCode = 200,
                UserAgent = "Mozilla/5.0", RefererHash = "", ContentClass = ContentClass.Page
            });
            first.Record("sig", new RequestSnapshot
            {
                Timestamp = now.AddSeconds(1), Path = "/b", Method = "GET", StatusCode = 200,
                UserAgent = "Mozilla/5.0", RefererHash = "", ContentClass = ContentClass.Asset
            });
            await Task.Delay(900);  // drain interval is 500ms; give it a beat
        }
        finally
        {
            first.Dispose();
        }

        var second = new WaveformHistoryStore(optsWrapper, NullLogger<WaveformHistoryStore>.Instance);
        try
        {
            await second.InitializeAsync();
            var loaded = await second.GetAsync("sig");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Snapshots.Count);
            Assert.Equal("/a", loaded.Snapshots[0].Path);
            Assert.Equal("/b", loaded.Snapshots[1].Path);
            // UA stored as hash, not the original string -- PII guard.
            Assert.NotEqual("Mozilla/5.0", loaded.Snapshots[0].UserAgent);
            Assert.False(string.IsNullOrEmpty(loaded.Snapshots[0].UserAgent));
        }
        finally
        {
            second.Dispose();
        }
    }
}
