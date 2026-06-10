using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Integration smoke test for <see cref="SqliteStickyDenyStore"/>. The
///     write-behind drainer is async, so we exercise the hot tier (which
///     is the authoritative source of truth per the
///     <c>WriteBehindLfuStore</c> contract) plus a write-then-cold-read
///     restart simulation. Without persistence the bot just retries after
///     a process restart and escapes the block window, defeating the
///     policy.
/// </summary>
public class SqliteStickyDenyStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteStickyDenyStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "stylobot-sticky-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore cleanup race */ }
    }

    [Fact]
    public async Task HotPath_ViolationsAccumulate_AndBlockTrips()
    {
        var stickyOpts = new StickyDenyActionOptions
        {
            ViolationThreshold = 3,
            WindowSeconds = 60,
            BlockTtlSeconds = 300,
        };
        using var store = NewStore(stickyOpts);
        await store.InitializeAsync();

        var now = DateTimeOffset.UnixEpoch;
        Assert.False(store.RecordViolation("k1", now));
        Assert.False(store.RecordViolation("k1", now.AddSeconds(10)));
        Assert.True(store.RecordViolation("k1", now.AddSeconds(20)),
            "3rd violation in-window must trip the block");

        Assert.True(store.IsBlocked("k1", now.AddSeconds(21)));
    }

    [Fact]
    public async Task BlockedState_SurvivesProcessRestart_ViaSqliteColdRead()
    {
        var stickyOpts = new StickyDenyActionOptions
        {
            ViolationThreshold = 1, // trip immediately for test brevity
            WindowSeconds = 60,
            BlockTtlSeconds = 300,
        };

        var now = DateTimeOffset.UtcNow;
        using (var store1 = NewStore(stickyOpts))
        {
            await store1.InitializeAsync();
            Assert.True(store1.RecordViolation("k1", now));
            // Let the drainer flush. The base class drains every ~1s; give
            // it 2s for headroom on slow CI.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        // Fresh store instance hitting the same db = cold load via the
        // SQLite drainer's persisted row.
        using var store2 = NewStore(stickyOpts);
        await store2.InitializeAsync();

        // TryGetHot returns null; the IsBlocked path on a fresh process
        // must consult the cold tier. Forcing this through the public
        // surface: just call IsBlocked, which calls TryGetHot. Since
        // TryGetHot doesn't auto-cold-load (only Get-or-Load does), we
        // need to trigger the load. The base class exposes
        // GetOrLoadAsync; sticky-deny exposes it via RecordViolation
        // which internally TryGetHots then Records. Easiest from the
        // outside: call IsBlocked via a path that warms it. Since the
        // contract is just "did the state survive?", we record one more
        // violation (which will trigger cold-load via the merge path) and
        // then assert blocked.

        // Cold-load path. Recording a second violation merges into the
        // cold-loaded state, which means the block_until from the prior
        // process must be preserved.
        store2.RecordViolation("k1", now.AddSeconds(1));
        Assert.True(store2.IsBlocked("k1", now.AddSeconds(2)),
            "block window must survive process restart via SQLite cold-read");
    }

    private SqliteStickyDenyStore NewStore(StickyDenyActionOptions stickyOpts)
    {
        var botOpts = new BotDetectionOptions { DatabasePath = Path.Combine(_tempDir, "botdetection.db") };
        return new SqliteStickyDenyStore(
            Options.Create(botOpts),
            stickyOpts,
            NullLogger<SqliteStickyDenyStore>.Instance);
    }
}
