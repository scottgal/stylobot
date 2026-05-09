using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

public class SqliteVectorCentroidStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbName;
    private readonly string _connectionString;
    private SqliteVectorCentroidStore _store = null!;
    // Keep the schema connection open for the lifetime of the test so the
    // shared-memory SQLite database is not destroyed between calls.
    private SqliteConnection _schemaConn = null!;

    public SqliteVectorCentroidStoreTests()
    {
        _dbName = $"centtest_{Guid.NewGuid():N}";
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
        _store = new SqliteVectorCentroidStore(_connectionString, NullLogger<SqliteVectorCentroidStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _schemaConn.DisposeAsync();
    }

    public void Dispose() { /* in-memory DB cleaned up automatically */ }

    [Fact]
    public async Task UpsertSignature_ThenGetRecent_ReturnsEntry()
    {
        var vector = new float[] { 1f, 2f, 3f };
        await _store.UpsertSignatureAsync("sig1", vector, wasBot: true, confidence: 0.9);

        var rows = await _store.GetRecentSignaturesAsync(10);
        Assert.Single(rows);
        Assert.Equal("sig1", rows[0].SignatureId);
        Assert.True(rows[0].WasBot);
        Assert.Equal(0.9, rows[0].Confidence, precision: 3);
        Assert.Equal(3, rows[0].Vector.Length);
    }

    [Fact]
    public async Task UpsertSignature_Overwrites_ExistingEntry()
    {
        await _store.UpsertSignatureAsync("sig2", new float[] { 1f }, wasBot: false, confidence: 0.5);
        await _store.UpsertSignatureAsync("sig2", new float[] { 2f, 3f }, wasBot: true, confidence: 0.95);

        var rows = await _store.GetRecentSignaturesAsync(10);
        var entry = rows.Single(r => r.SignatureId == "sig2");
        Assert.True(entry.WasBot);
        Assert.Equal(2, entry.Vector.Length);
    }

    [Fact]
    public async Task PruneSignatures_DeletesOldRows()
    {
        await _store.UpsertSignatureAsync("old", new float[] { 1f }, wasBot: false, confidence: 0.3);
        await _store.PruneSignaturesOlderThanAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        var rows = await _store.GetRecentSignaturesAsync(10);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task UpsertSession_ThenGetRecent_ReturnsEntry()
    {
        var meta = new SessionCentroidRow
        {
            SignatureId = "sessSig1",
            Vector = new float[] { 1f, 2f },
            IsBot = true,
            BotProbability = 0.8,
            CompressionLevel = 1,
            Priority = 0.9,
            ClusterId = "cluster1"
        };
        await _store.UpsertSessionAsync(meta);

        var rows = await _store.GetRecentSessionsAsync(10);
        Assert.Single(rows);
        Assert.Equal("sessSig1", rows[0].SignatureId);
        Assert.Equal(1, rows[0].CompressionLevel);
        Assert.Equal("cluster1", rows[0].ClusterId);
    }

    [Fact]
    public async Task UpsertIntent_ThenGetRecent_ReturnsEntry()
    {
        await _store.UpsertIntentAsync("intentSig1", new float[] { 0.5f, 0.5f }, 0.75, "scanning");

        var rows = await _store.GetRecentIntentsAsync(10);
        Assert.Single(rows);
        Assert.Equal("intentSig1", rows[0].SignatureId);
        Assert.Equal("scanning", rows[0].IntentCategory);
    }

    [Fact]
    public async Task FloatRoundtrip_PreservesValues()
    {
        var original = new float[] { 1.23f, -4.56f, 0f, float.MaxValue };
        await _store.UpsertSignatureAsync("roundtrip", original, false, 0.5);
        var rows = await _store.GetRecentSignaturesAsync(1);
        var recovered = rows[0].Vector;
        Assert.Equal(original.Length, recovered.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], recovered[i]);
    }
}
