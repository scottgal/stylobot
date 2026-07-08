using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

// ============================================================================
// SqliteSignatureCentroidStore -- WriteBehindLfuStore subclass tests
// ============================================================================

public class SignatureCentroidWriteBehindStoreTests : IAsyncLifetime
{
    private SqliteSignatureCentroidStore _store = null!;
    private string _dbDir = null!;
    private string _dbFile = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"cw_sig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "botdetection.db")
        });
        _store = new SqliteSignatureCentroidStore(opts, NullLogger<SqliteSignatureCentroidStore>.Instance);
        await _store.InitializeAsync();
        // The store creates signature_centroids.db in the same directory as DatabasePath
        _dbFile = Path.Combine(_dbDir, "signature_centroids.db");
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // (a) RecordSignature then TryGetHot returns the entry immediately.
    [Fact]
    public void RecordSignature_HotCachePopulatedImmediately()
    {
        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        _store.RecordSignature("sig-hot", vector, wasBot: true, confidence: 0.85);

        var entry = _store.TryGetHot("sig-hot");
        Assert.NotNull(entry);
        Assert.True(entry!.WasBot);
        Assert.Equal(0.85, entry.Confidence, precision: 3);
        Assert.Equal(3, entry.Vector.Length);
    }

    // (b) After drain interval (~500ms), row appears in SQLite.
    [Fact]
    public async Task RecordSignature_PersistedToDurableTierWithinDrainInterval()
    {
        var vector = new float[] { 1f, 2f };
        _store.RecordSignature("sig-durable", vector, wasBot: false, confidence: 0.4);

        // Poll up to 2s for the drainer to flush.
        var found = false;
        for (var i = 0; i < 20 && !found; i++)
        {
            await Task.Delay(100);
            await using var conn = new SqliteConnection($"Data Source={_dbFile}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM signature_centroids WHERE signature_id = 'sig-durable'";
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            found = count == 1;
        }
        Assert.True(found, "Row did not appear in SQLite within 2s drain window");
    }

    // (c) ColdnessScore: high-necessity (uncertain+high-threat) > low-necessity (confident+harmless).
    [Fact]
    public void ColdnessScore_HighNecessityEntryOutscoresLowNecessity()
    {
        // High necessity: confidence near threshold (0.70) AND wasBot=true (threat=confidence).
        _store.RecordSignature("sig-hot-threat", new float[] { 1f }, wasBot: true, confidence: 0.70);
        // Low necessity: confident harmless (far from threshold, no threat).
        _store.RecordSignature("sig-cold-harmless", new float[] { 1f }, wasBot: false, confidence: 0.05);

        var highScore = _store.GetColdnessScore("sig-hot-threat");
        var lowScore  = _store.GetColdnessScore("sig-cold-harmless");
        Assert.True(highScore > lowScore,
            $"High-necessity entry (score={highScore}) should outrank low-necessity (score={lowScore})");
    }

    // (d) Two Records for same signature -> Count == 1, merged latest values.
    [Fact]
    public void RecordSignature_SameKey_DedupsInHotCache()
    {
        _store.RecordSignature("sig-dedup", new float[] { 1f }, wasBot: false, confidence: 0.3);
        _store.RecordSignature("sig-dedup", new float[] { 2f, 3f }, wasBot: true, confidence: 0.9);

        Assert.Equal(1, _store.Count);
        var entry = _store.TryGetHot("sig-dedup");
        Assert.NotNull(entry);
        Assert.True(entry!.WasBot);
        Assert.Equal(0.9, entry.Confidence, precision: 3);
        Assert.Equal(2, entry.Vector.Length);
    }
}

// ============================================================================
// SqliteSessionCentroidStore -- WriteBehindLfuStore subclass tests
// ============================================================================

public class SessionCentroidWriteBehindStoreTests : IAsyncLifetime
{
    private SqliteSessionCentroidStore _store = null!;
    private string _dbDir = null!;
    private string _dbFile = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"cw_sess_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "botdetection.db")
        });
        _store = new SqliteSessionCentroidStore(opts, NullLogger<SqliteSessionCentroidStore>.Instance);
        await _store.InitializeAsync();
        _dbFile = Path.Combine(_dbDir, "session_centroids.db");
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // (a) RecordSession then TryGetHot returns the entry immediately.
    [Fact]
    public void RecordSession_HotCachePopulatedImmediately()
    {
        var row = new SessionCentroidRow
        {
            SignatureId = "sess-hot",
            Vector = new float[] { 0.1f, 0.2f },
            IsBot = true,
            BotProbability = 0.88,
            CompressionLevel = 1,
            Priority = 0.9
        };
        _store.RecordSession(row);

        var entry = _store.TryGetHot("sess-hot");
        Assert.NotNull(entry);
        Assert.True(entry!.IsBot);
        Assert.Equal(0.88, entry.BotProbability, precision: 3);
    }

    // (b) After drain interval, row appears in SQLite.
    [Fact]
    public async Task RecordSession_PersistedToDurableTierWithinDrainInterval()
    {
        _store.RecordSession(new SessionCentroidRow
        {
            SignatureId = "sess-durable",
            Vector = new float[] { 1f },
            IsBot = false,
            BotProbability = 0.2
        });

        var found = false;
        for (var i = 0; i < 20 && !found; i++)
        {
            await Task.Delay(100);
            await using var conn = new SqliteConnection($"Data Source={_dbFile}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM session_centroids WHERE signature_id = 'sess-durable'";
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            found = count == 1;
        }
        Assert.True(found, "Session row did not appear in SQLite within 2s drain window");
    }

    // (c) ColdnessScore: high-necessity (bot+high-prob near threshold) > low-necessity.
    [Fact]
    public void ColdnessScore_HighNecessitySessionOutscoresLowNecessity()
    {
        _store.RecordSession(new SessionCentroidRow
        {
            SignatureId = "sess-hot-threat",
            Vector = new float[] { 1f },
            IsBot = true,
            BotProbability = 0.70  // at threshold -> high uncertainty + high threat
        });
        _store.RecordSession(new SessionCentroidRow
        {
            SignatureId = "sess-cold-harmless",
            Vector = new float[] { 1f },
            IsBot = false,
            BotProbability = 0.05  // far below threshold, no threat
        });

        var highScore = _store.GetColdnessScore("sess-hot-threat");
        var lowScore  = _store.GetColdnessScore("sess-cold-harmless");
        Assert.True(highScore > lowScore,
            $"High-necessity session (score={highScore}) should outrank low-necessity (score={lowScore})");
    }

    // (d) Two Records for same signature -> Count == 1.
    [Fact]
    public void RecordSession_SameKey_DedupsInHotCache()
    {
        _store.RecordSession(new SessionCentroidRow
        {
            SignatureId = "sess-dedup",
            Vector = new float[] { 1f },
            IsBot = false,
            BotProbability = 0.2
        });
        _store.RecordSession(new SessionCentroidRow
        {
            SignatureId = "sess-dedup",
            Vector = new float[] { 2f, 3f },
            IsBot = true,
            BotProbability = 0.9
        });

        Assert.Equal(1, _store.Count);
        var entry = _store.TryGetHot("sess-dedup");
        Assert.NotNull(entry);
        Assert.True(entry!.IsBot);
        Assert.Equal(0.9, entry.BotProbability, precision: 3);
    }
}

// ============================================================================
// SqliteIntentCentroidStore -- WriteBehindLfuStore subclass tests
// ============================================================================

public class IntentCentroidWriteBehindStoreTests : IAsyncLifetime
{
    private SqliteIntentCentroidStore _store = null!;
    private string _dbDir = null!;
    private string _dbFile = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"cw_intent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "botdetection.db")
        });
        _store = new SqliteIntentCentroidStore(opts, NullLogger<SqliteIntentCentroidStore>.Instance);
        await _store.InitializeAsync();
        _dbFile = Path.Combine(_dbDir, "intent_centroids.db");
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // (a) RecordIntent then TryGetHot returns the entry immediately.
    [Fact]
    public void RecordIntent_HotCachePopulatedImmediately()
    {
        _store.RecordIntent("intent-hot", new float[] { 0.5f, 0.5f }, threatScore: 0.8, category: "scanning");

        var entry = _store.TryGetHot("intent-hot");
        Assert.NotNull(entry);
        Assert.Equal("scanning", entry!.IntentCategory);
        Assert.Equal(0.8, entry.ThreatScore, precision: 3);
    }

    // (b) After drain interval, row appears in SQLite.
    [Fact]
    public async Task RecordIntent_PersistedToDurableTierWithinDrainInterval()
    {
        _store.RecordIntent("intent-durable", new float[] { 0.3f }, threatScore: 0.5, category: "crawling");

        var found = false;
        for (var i = 0; i < 20 && !found; i++)
        {
            await Task.Delay(100);
            await using var conn = new SqliteConnection($"Data Source={_dbFile}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM intent_centroids WHERE signature_id = 'intent-durable'";
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            found = count == 1;
        }
        Assert.True(found, "Intent row did not appear in SQLite within 2s drain window");
    }

    // (c) ColdnessScore: high threat > low threat (intent store is threat-driven).
    [Fact]
    public void ColdnessScore_HighThreatIntentOutscoresLowThreat()
    {
        _store.RecordIntent("intent-high-threat", new float[] { 1f }, threatScore: 0.95, category: "scanning");
        _store.RecordIntent("intent-low-threat",  new float[] { 1f }, threatScore: 0.02, category: "crawling");

        var highScore = _store.GetColdnessScore("intent-high-threat");
        var lowScore  = _store.GetColdnessScore("intent-low-threat");
        Assert.True(highScore > lowScore,
            $"High-threat intent (score={highScore}) should outrank low-threat (score={lowScore})");
    }

    // (d) Two Records for same signature -> Count == 1, merged latest values.
    [Fact]
    public void RecordIntent_SameKey_DedupsInHotCache()
    {
        _store.RecordIntent("intent-dedup", new float[] { 0.1f }, threatScore: 0.2, category: "crawling");
        _store.RecordIntent("intent-dedup", new float[] { 0.9f, 0.8f }, threatScore: 0.95, category: "scraping");

        Assert.Equal(1, _store.Count);
        var entry = _store.TryGetHot("intent-dedup");
        Assert.NotNull(entry);
        Assert.Equal("scraping", entry!.IntentCategory);
        Assert.Equal(0.95, entry.ThreatScore, precision: 3);
        Assert.Equal(2, entry.Vector.Length);
    }
}
