using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
/// Verifies that the connection-accepting Upsert overloads on the three centroid stores
/// allow a single shared SqliteConnection to write all three tables (Task 2 of the
/// single-writer centroid drain fix).
/// </summary>
public class CentroidStoreSharedConnectionTests : IAsyncLifetime
{
    private readonly string _dbName;
    private readonly string _connectionString;
    private SqliteSignatureCentroidStore _signatureStore = null!;
    private SqliteSessionCentroidStore _sessionStore = null!;
    private SqliteIntentCentroidStore _intentStore = null!;

    // Held open so the shared-memory DB survives between operations.
    private SqliteConnection _schemaConn = null!;

    public CentroidStoreSharedConnectionTests()
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

    // ── Shared-connection overload tests ─────────────────────────────────────

    [Fact]
    public async Task SharedConnection_AllThreeStores_WriteToCorrectTables()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Write all three through the same connection.
        await _signatureStore.UpsertSignatureAsync(conn, "shared_sig", new float[] { 1f, 2f, 3f }, wasBot: true, confidence: 0.85);

        var sessionRow = new SessionCentroidRow
        {
            SignatureId = "shared_sess",
            Vector = new float[] { 0.1f, 0.2f },
            IsBot = false,
            BotProbability = 0.3,
            CompressionLevel = 1,
            Priority = 0.7
        };
        await _sessionStore.UpsertSessionAsync(conn, sessionRow);

        await _intentStore.UpsertIntentAsync(conn, "shared_intent", new float[] { 0.5f }, 0.6, "scanning");

        // Verify signature row landed.
        var sigRows = await _signatureStore.GetRecentSignaturesAsync(10);
        var sigEntry = Assert.Single(sigRows, r => r.SignatureId == "shared_sig");
        Assert.True(sigEntry.WasBot);
        Assert.Equal(0.85, sigEntry.Confidence, precision: 3);
        Assert.Equal(3, sigEntry.Vector.Length);

        // Verify session row landed.
        var sessRows = await _sessionStore.GetRecentSessionsAsync(10);
        var sessEntry = Assert.Single(sessRows, r => r.SignatureId == "shared_sess");
        Assert.False(sessEntry.IsBot);
        Assert.Equal(0.3, sessEntry.BotProbability, precision: 3);
        Assert.Equal(1, sessEntry.CompressionLevel);

        // Verify intent row landed.
        var intentRows = await _intentStore.GetRecentIntentsAsync(10);
        var intentEntry = Assert.Single(intentRows, r => r.SignatureId == "shared_intent");
        Assert.Equal("scanning", intentEntry.IntentCategory);
        Assert.Equal(0.6, intentEntry.ThreatScore, precision: 3);
    }

    [Fact]
    public async Task SharedConnection_SessionStore_NullableVectors_RoundTrip()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var row = new SessionCentroidRow
        {
            SignatureId    = "shared_nullable",
            Vector         = new float[] { 1f },
            VelocityVector = new float[] { 0.1f, 0.2f },
            VarianceVector = null,
            FreqFingerprint = new float[] { 0.5f }
        };
        await _sessionStore.UpsertSessionAsync(conn, row);

        var rows = await _sessionStore.GetRecentSessionsAsync(10);
        var entry = rows.Single(r => r.SignatureId == "shared_nullable");
        Assert.NotNull(entry.VelocityVector);
        Assert.Equal(2, entry.VelocityVector!.Length);
        Assert.Null(entry.VarianceVector);
        Assert.NotNull(entry.FreqFingerprint);
    }

    [Fact]
    public async Task SharedConnection_FloatRoundtrip_PreservesValues()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var original = new float[] { 1.23f, -4.56f, 0f, float.MaxValue };
        await _signatureStore.UpsertSignatureAsync(conn, "shared_roundtrip", original, wasBot: false, confidence: 0.5);

        var rows = await _signatureStore.GetRecentSignaturesAsync(10);
        var recovered = rows.Single(r => r.SignatureId == "shared_roundtrip").Vector;
        Assert.Equal(original.Length, recovered.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], recovered[i]);
    }

    // ── Back-compat: original public overloads (no connection arg) still work ─

    [Fact]
    public async Task BackCompat_SignatureStore_NoConnectionArg_StillWrites()
    {
        await _signatureStore.UpsertSignatureAsync("compat_sig", new float[] { 9f }, wasBot: false, confidence: 0.4);
        var rows = await _signatureStore.GetRecentSignaturesAsync(10);
        Assert.Contains(rows, r => r.SignatureId == "compat_sig" && !r.WasBot);
    }

    [Fact]
    public async Task BackCompat_SessionStore_NoConnectionArg_StillWrites()
    {
        var row = new SessionCentroidRow
        {
            SignatureId = "compat_sess",
            Vector = new float[] { 7f },
            IsBot = true,
            BotProbability = 0.9
        };
        await _sessionStore.UpsertSessionAsync(row);
        var rows = await _sessionStore.GetRecentSessionsAsync(10);
        Assert.Contains(rows, r => r.SignatureId == "compat_sess" && r.IsBot);
    }

    [Fact]
    public async Task BackCompat_IntentStore_NoConnectionArg_StillWrites()
    {
        await _intentStore.UpsertIntentAsync("compat_intent", new float[] { 0.2f }, 0.3, "crawling");
        var rows = await _intentStore.GetRecentIntentsAsync(10);
        Assert.Contains(rows, r => r.SignatureId == "compat_intent" && r.IntentCategory == "crawling");
    }
}
