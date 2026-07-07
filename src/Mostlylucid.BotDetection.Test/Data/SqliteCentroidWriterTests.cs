using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Centroids;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
/// Integration tests for SqliteCentroidWriter (Task 3).
/// Uses a real shared-memory SQLite DB with a kept-open schema connection to
/// prevent the in-memory DB from being destroyed between operations.
/// </summary>
public class SqliteCentroidWriterTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbName;
    private readonly string _connectionString;
    private SqliteSignatureCentroidStore _signatureStore = null!;
    private SqliteSessionCentroidStore _sessionStore = null!;
    private SqliteIntentCentroidStore _intentStore = null!;
    // Keep open so the shared-memory DB survives between operations.
    private SqliteConnection _schemaConn = null!;

    public SqliteCentroidWriterTests()
    {
        _dbName = $"cw_{Guid.NewGuid():N}";
        _connectionString = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
    }

    public async Task InitializeAsync()
    {
        _schemaConn = new SqliteConnection(_connectionString);
        await _schemaConn.OpenAsync();
        await using var cmd = _schemaConn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS signature_centroids (
                signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                was_bot INTEGER NOT NULL DEFAULT 0, confidence REAL NOT NULL DEFAULT 0.5,
                access_count INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS session_centroids (
                signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                velocity_vector BLOB, variance_vector BLOB, freq_fingerprint BLOB,
                cluster_id TEXT, compression_level INTEGER NOT NULL DEFAULT 0,
                is_bot INTEGER NOT NULL DEFAULT 0, bot_probability REAL NOT NULL DEFAULT 0.0,
                priority REAL NOT NULL DEFAULT 0.5, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS intent_centroids (
                signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                threat_score REAL NOT NULL DEFAULT 0.0, intent_category TEXT NOT NULL DEFAULT 'unknown',
                updated_at INTEGER NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();

        _signatureStore = new SqliteSignatureCentroidStore(_connectionString, NullLogger<SqliteSignatureCentroidStore>.Instance);
        _sessionStore   = new SqliteSessionCentroidStore(_connectionString, NullLogger<SqliteSessionCentroidStore>.Instance);
        _intentStore    = new SqliteIntentCentroidStore(_connectionString, NullLogger<SqliteIntentCentroidStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _schemaConn.DisposeAsync();
    }

    public void Dispose()
    {
        _schemaConn.Dispose();
    }

    private SqliteCentroidWriter CreateWriter(int capacity = 512)
    {
        var opts = Options.Create(new CentroidWriterOptions
        {
            ChannelCapacity = capacity,
            DropLogCadence = TimeSpan.FromMinutes(1),
            ReconnectBackoff = TimeSpan.FromMilliseconds(10),
        });
        return new SqliteCentroidWriter(
            _connectionString,
            _sessionStore,
            _signatureStore,
            _intentStore,
            opts,
            NullLogger<SqliteCentroidWriter>.Instance);
    }

    /// <summary>
    /// Polls until predicate is true or timeout elapses.
    /// </summary>
    private static async Task PollUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    // ── (a) Single SignatureCentroidWrite lands in signature_centroids ────────

    [Fact]
    public async Task Enqueue_SignatureWrite_RowLandsInTable()
    {
        using var writer = CreateWriter();
        writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite("sig_a", new float[] { 1f, 2f }, true, 0.9));

        await PollUntilAsync(async () =>
        {
            var rows = await _signatureStore.GetRecentSignaturesAsync(10);
            return rows.Any(r => r.SignatureId == "sig_a");
        }, TimeSpan.FromSeconds(2));

        var rows = await _signatureStore.GetRecentSignaturesAsync(10);
        var entry = Assert.Single(rows, r => r.SignatureId == "sig_a");
        Assert.True(entry.WasBot);
        Assert.Equal(0.9, entry.Confidence, precision: 3);
        Assert.Equal(2, entry.Vector.Length);
    }

    // ── (b) One of each type: all three tables receive their row ─────────────

    [Fact]
    public async Task Enqueue_AllThreeTypes_AllTablesReceiveRows()
    {
        using var writer = CreateWriter();

        var sessionRow = new SessionCentroidRow
        {
            SignatureId = "sess_b",
            Vector = new float[] { 0.5f },
            IsBot = false,
            BotProbability = 0.2,
            CompressionLevel = 0,
            Priority = 0.5,
        };

        writer.Enqueue(new CentroidWriteMessage.SessionCentroidWrite(sessionRow));
        writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite("sig_b", new float[] { 3f, 4f }, false, 0.4));
        writer.Enqueue(new CentroidWriteMessage.IntentCentroidWrite("intent_b", new float[] { 0.1f }, 0.6, "scanning"));

        await PollUntilAsync(async () =>
        {
            var sigs  = await _signatureStore.GetRecentSignaturesAsync(10);
            var sess  = await _sessionStore.GetRecentSessionsAsync(10);
            var ints  = await _intentStore.GetRecentIntentsAsync(10);
            return sigs.Any(r => r.SignatureId == "sig_b")
                && sess.Any(r => r.SignatureId == "sess_b")
                && ints.Any(r => r.SignatureId == "intent_b");
        }, TimeSpan.FromSeconds(2));

        var sigRows    = await _signatureStore.GetRecentSignaturesAsync(10);
        var sessRows   = await _sessionStore.GetRecentSessionsAsync(10);
        var intentRows = await _intentStore.GetRecentIntentsAsync(10);

        Assert.Single(sigRows,    r => r.SignatureId == "sig_b");
        Assert.Single(sessRows,   r => r.SignatureId == "sess_b");
        Assert.Single(intentRows, r => r.SignatureId == "intent_b");
    }

    // ── (c) Fail-closed: a bad write does not kill the drain ─────────────────

    [Fact]
    public async Task FailClosed_BadWriteFollowedByGoodWrite_GoodMessagePersists()
    {
        // Drop the signature_centroids table to make the first write throw
        // a SqliteException (no such table), then restore it so the session
        // store (different table name) still works.  We write to signature
        // twice: once with the table gone (throws), once with it restored.
        // The drain must survive the throw and land the session write.

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Temporarily drop the signature_centroids table.
        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE IF EXISTS signature_centroids";
            await drop.ExecuteNonQueryAsync();
        }

        using var writer = CreateWriter();

        // This will throw inside the drain because the table is gone.
        writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite("bad_sig_c", new float[] { 1f }, true, 0.9));

        // Give the drain a moment to attempt + fail the bad message.
        await Task.Delay(150);

        // Restore the table.
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS signature_centroids (
                    signature_id TEXT PRIMARY KEY, vector BLOB NOT NULL,
                    was_bot INTEGER NOT NULL DEFAULT 0, confidence REAL NOT NULL DEFAULT 0.5,
                    access_count INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL);
                """;
            await create.ExecuteNonQueryAsync();
        }

        // Enqueue a good message AFTER the table is restored.
        writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite("good_sig_c", new float[] { 2f }, false, 0.3));

        await PollUntilAsync(async () =>
        {
            var rows = await _signatureStore.GetRecentSignaturesAsync(10);
            return rows.Any(r => r.SignatureId == "good_sig_c");
        }, TimeSpan.FromSeconds(3));

        var finalRows = await _signatureStore.GetRecentSignaturesAsync(10);
        Assert.Contains(finalRows, r => r.SignatureId == "good_sig_c");
    }

    // ── (d) Dispose drains the last queued message ────────────────────────────

    [Fact]
    public async Task Dispose_DrainsLastEnqueuedMessage()
    {
        var writer = CreateWriter();
        writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite("sig_d", new float[] { 5f, 6f }, true, 0.7));

        // Dispose immediately; graceful drain must flush the queued message.
        writer.Dispose();

        // Poll: the row should have landed synchronously during Dispose.
        await PollUntilAsync(async () =>
        {
            var rows = await _signatureStore.GetRecentSignaturesAsync(10);
            return rows.Any(r => r.SignatureId == "sig_d");
        }, TimeSpan.FromSeconds(2));

        var finalRows = await _signatureStore.GetRecentSignaturesAsync(10);
        Assert.Contains(finalRows, r => r.SignatureId == "sig_d");
    }

    // ── (e) DroppedCount is 0 in the normal (uncongested) path ───────────────

    [Fact]
    public async Task DroppedCount_NormalPath_IsZero()
    {
        using var writer = CreateWriter(capacity: 512);
        writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite("sig_e", new float[] { 7f }, false, 0.5));

        await PollUntilAsync(async () =>
        {
            var rows = await _signatureStore.GetRecentSignaturesAsync(10);
            return rows.Any(r => r.SignatureId == "sig_e");
        }, TimeSpan.FromSeconds(2));

        Assert.Equal(0L, writer.DroppedCount);
    }

    // ── (f) Channel flood: itemDropped callback counts DropOldest evictions ──────

    [Fact]
    public async Task DroppedCount_UnderFlood_Increments()
    {
        // With BoundedChannelFullMode.DropOldest and capacity=1, the channel evicts
        // the oldest item each time a new one is written while full.  TryWrite always
        // returns true so a counter gated on its return value stays 0.  The fix is the
        // itemDropped callback passed to Channel.CreateBounded: the channel invokes it
        // for every eviction, so DroppedCount now reflects real drops.
        //
        // A stalled drain (bad connection string -> SqliteException -> reconnect backoff)
        // keeps the single slot occupied so subsequent writes trigger evictions.
        const string badConnString = "Data Source=/tmp/__centroid_flood_test__.db";

        // Delete any leftover from a prior run so the DB starts without the required
        // tables, causing every write to throw and the drain to stall in backoff.
        if (File.Exists("/tmp/__centroid_flood_test__.db"))
            File.Delete("/tmp/__centroid_flood_test__.db");

        var opts = Options.Create(new CentroidWriterOptions
        {
            ChannelCapacity = 1,
            DropLogCadence = TimeSpan.FromMinutes(1),
            ReconnectBackoff = TimeSpan.FromMilliseconds(50),
        });
        using var writer = new SqliteCentroidWriter(
            badConnString,
            _sessionStore,
            _signatureStore,
            _intentStore,
            opts,
            NullLogger<SqliteCentroidWriter>.Instance);

        // Flood: enqueue 50 messages into a capacity-1 channel with a stalled drain.
        // Each enqueue after the first evicts the currently-queued item, firing the
        // itemDropped callback and incrementing DroppedCount.
        for (var i = 0; i < 50; i++)
        {
            writer.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite(
                $"flood_{i}", new float[] { (float)i }, true, 0.9));
            // Small yield so the drain can attempt (and fail) the first item,
            // freeing the slot for DropOldest to fill+evict on subsequent writes.
            if (i == 0) await Task.Delay(20);
        }

        // Poll up to 2s: DroppedCount must be > 0.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (writer.DroppedCount == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(writer.DroppedCount > 0,
            $"Expected DroppedCount > 0 after flood but got {writer.DroppedCount}");

        // QueueDepth must not exceed channel capacity.
        Assert.True(writer.QueueDepth <= 1,
            $"QueueDepth {writer.QueueDepth} exceeded ChannelCapacity 1 under DropOldest");
    }
}
