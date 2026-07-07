using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
/// Tests for the three split centroid stores: SqliteSignatureCentroidStore,
/// SqliteSessionCentroidStore, and SqliteIntentCentroidStore.
/// Uses a shared-memory SQLite DB with a kept-open _schemaConn (IAsyncLifetime)
/// to prevent the in-memory DB from being destroyed between operations.
/// </summary>
public class SqliteVectorCentroidStoreTests : IAsyncLifetime
{
    private readonly string _dbName;
    private readonly string _connectionString;
    private SqliteSignatureCentroidStore _signatureStore = null!;
    private SqliteSessionCentroidStore _sessionStore = null!;
    private SqliteIntentCentroidStore _intentStore = null!;
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

        _signatureStore = new SqliteSignatureCentroidStore(_connectionString, NullLogger<SqliteSignatureCentroidStore>.Instance);
        _sessionStore   = new SqliteSessionCentroidStore(_connectionString, NullLogger<SqliteSessionCentroidStore>.Instance);
        _intentStore    = new SqliteIntentCentroidStore(_connectionString, NullLogger<SqliteIntentCentroidStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _schemaConn.DisposeAsync();
    }

    // ── Signature centroid store ─────────────────────────────────────────────

    [Fact]
    public async Task UpsertSignature_ThenGetRecent_ReturnsEntry()
    {
        var vector = new float[] { 1f, 2f, 3f };
        await _signatureStore.UpsertSignatureAsync("sig1", vector, wasBot: true, confidence: 0.9);

        var rows = await _signatureStore.GetRecentSignaturesAsync(10);
        var entry = Assert.Single(rows, r => r.SignatureId == "sig1");
        Assert.True(entry.WasBot);
        Assert.Equal(0.9, entry.Confidence, precision: 3);
        Assert.Equal(3, entry.Vector.Length);
    }

    [Fact]
    public async Task UpsertSignature_Overwrites_ExistingEntry()
    {
        await _signatureStore.UpsertSignatureAsync("sig2", new float[] { 1f }, wasBot: false, confidence: 0.5);
        await _signatureStore.UpsertSignatureAsync("sig2", new float[] { 2f, 3f }, wasBot: true, confidence: 0.95);

        var rows = await _signatureStore.GetRecentSignaturesAsync(10);
        var entry = rows.Single(r => r.SignatureId == "sig2");
        Assert.True(entry.WasBot);
        Assert.Equal(2, entry.Vector.Length);
    }

    [Fact]
    public async Task PruneSignatures_DeletesOldRows()
    {
        await _signatureStore.UpsertSignatureAsync("old_sig", new float[] { 1f }, wasBot: false, confidence: 0.3);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        await _signatureStore.PruneSignaturesOlderThanAsync(cutoff);

        var rows = await _signatureStore.GetRecentSignaturesAsync(10);
        Assert.DoesNotContain(rows, r => r.SignatureId == "old_sig");
    }

    [Fact]
    public async Task FloatRoundtrip_PreservesValues()
    {
        var original = new float[] { 1.23f, -4.56f, 0f, float.MaxValue };
        await _signatureStore.UpsertSignatureAsync("roundtrip", original, false, 0.5);
        var rows = await _signatureStore.GetRecentSignaturesAsync(1);
        var recovered = rows.Single(r => r.SignatureId == "roundtrip").Vector;
        Assert.Equal(original.Length, recovered.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], recovered[i]);
    }

    // ── Session centroid store ───────────────────────────────────────────────

    [Fact]
    public async Task UpsertSession_ThenGetRecent_ReturnsEntry()
    {
        var row = new SessionCentroidRow
        {
            SignatureId = "sessSig1",
            Vector = new float[] { 1f, 2f },
            IsBot = true,
            BotProbability = 0.8,
            CompressionLevel = 1,
            Priority = 0.9,
            ClusterId = "cluster1"
        };
        await _sessionStore.UpsertSessionAsync(row);

        var rows = await _sessionStore.GetRecentSessionsAsync(10);
        var entry = Assert.Single(rows, r => r.SignatureId == "sessSig1");
        Assert.Equal(1, entry.CompressionLevel);
        Assert.Equal("cluster1", entry.ClusterId);
        Assert.True(entry.IsBot);
        Assert.Equal(0.8, entry.BotProbability, precision: 3);
    }

    [Fact]
    public async Task UpsertSession_WithNullableVectors_RoundTrips()
    {
        var row = new SessionCentroidRow
        {
            SignatureId     = "sess_nullable",
            Vector          = new float[] { 1f },
            VelocityVector  = new float[] { 0.1f, 0.2f },
            VarianceVector  = null,
            FreqFingerprint = new float[] { 0.5f },
        };
        await _sessionStore.UpsertSessionAsync(row);

        var rows = await _sessionStore.GetRecentSessionsAsync(10);
        var entry = rows.Single(r => r.SignatureId == "sess_nullable");
        Assert.NotNull(entry.VelocityVector);
        Assert.Equal(2, entry.VelocityVector!.Length);
        Assert.Null(entry.VarianceVector);
        Assert.NotNull(entry.FreqFingerprint);
    }

    [Fact]
    public async Task PruneSessions_DeletesOldRows()
    {
        var row = new SessionCentroidRow { SignatureId = "old_sess", Vector = new float[] { 1f } };
        await _sessionStore.UpsertSessionAsync(row);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        await _sessionStore.PruneSessionsOlderThanAsync(cutoff);

        var rows = await _sessionStore.GetRecentSessionsAsync(10);
        Assert.DoesNotContain(rows, r => r.SignatureId == "old_sess");
    }

    // ── Intent centroid store ────────────────────────────────────────────────

    [Fact]
    public async Task UpsertIntent_ThenGetRecent_ReturnsEntry()
    {
        await _intentStore.UpsertIntentAsync("intentSig1", new float[] { 0.5f, 0.5f }, 0.75, "scanning");

        var rows = await _intentStore.GetRecentIntentsAsync(10);
        var entry = Assert.Single(rows, r => r.SignatureId == "intentSig1");
        Assert.Equal("scanning", entry.IntentCategory);
        Assert.Equal(0.75, entry.ThreatScore, precision: 3);
    }

    [Fact]
    public async Task UpsertIntent_Overwrites_ExistingEntry()
    {
        await _intentStore.UpsertIntentAsync("intentSig2", new float[] { 0.1f }, 0.2, "crawling");
        await _intentStore.UpsertIntentAsync("intentSig2", new float[] { 0.9f, 0.8f }, 0.95, "scraping");

        var rows = await _intentStore.GetRecentIntentsAsync(10);
        var entry = rows.Single(r => r.SignatureId == "intentSig2");
        Assert.Equal("scraping", entry.IntentCategory);
        Assert.Equal(0.95, entry.ThreatScore, precision: 3);
        Assert.Equal(2, entry.Vector.Length);
    }

    [Fact]
    public async Task PruneIntents_DeletesOldRows()
    {
        await _intentStore.UpsertIntentAsync("old_intent", new float[] { 0.1f }, 0.5, "unknown");
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        await _intentStore.PruneIntentsOlderThanAsync(cutoff);

        var rows = await _intentStore.GetRecentIntentsAsync(10);
        Assert.DoesNotContain(rows, r => r.SignatureId == "old_intent");
    }
}
