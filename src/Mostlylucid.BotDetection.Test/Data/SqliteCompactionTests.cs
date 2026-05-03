using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Tests for SqliteSessionStore.CompactSignatureSessionsAsync —
///     specifically the frequency_centroid column write path and the
///     CompactionResult.FrequencyCentroid field introduced for cross-session
///     rhythm detection.
/// </summary>
public class SqliteCompactionTests : IAsyncLifetime
{
    private SqliteSessionStore _store = null!;
    private string _dbDir = null!;
    private static readonly int VecDims = SessionVectorizer.Dimensions;

    public async Task InitializeAsync()
    {
        // Pass DatabasePath as a file path with extension so GetDirectoryName returns the right dir
        _dbDir = Path.Combine(Path.GetTempPath(), $"compaction-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var dbFilePath = Path.Combine(_dbDir, "botdetection.db");

        var opts = Options.Create(new BotDetectionOptions { DatabasePath = dbFilePath });
        _store = new SqliteSessionStore(NullLogger<SqliteSessionStore>.Instance, opts);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
    }

    // ============================================================
    // No sessions to compact
    // ============================================================

    [Fact]
    public async Task CompactSignatureSessions_NoRows_ReturnsZeroCount()
    {
        // Signature exists but has no sessions yet
        await SeedSignatureAsync("sig-empty");

        var result = await _store.CompactSignatureSessionsAsync("sig-empty", keepCount: 5);

        Assert.Equal(0, result.CompactedCount);
        Assert.Null(result.FrequencyCentroid);
    }

    [Fact]
    public async Task CompactSignatureSessions_FewerThanKeepCount_ReturnsZeroCount()
    {
        await SeedSignatureAsync("sig-few");
        await SeedSessionAsync("sig-few", hasFp: false);
        await SeedSessionAsync("sig-few", hasFp: false);

        // keepCount = 5 means nothing to compact when only 2 sessions
        var result = await _store.CompactSignatureSessionsAsync("sig-few", keepCount: 5);

        Assert.Equal(0, result.CompactedCount);
    }

    // ============================================================
    // Compaction when sessions have no frequency fingerprints
    // ============================================================

    [Fact]
    public async Task CompactSignatureSessions_NoFingerprints_FrequencyCentroidIsNull()
    {
        await SeedSignatureAsync("sig-nofp");
        for (var i = 0; i < 6; i++)
            await SeedSessionAsync("sig-nofp", hasFp: false);

        // keepCount = 2, so 4 sessions compacted
        var result = await _store.CompactSignatureSessionsAsync("sig-nofp", keepCount: 2);

        Assert.Equal(4, result.CompactedCount);
        Assert.Null(result.FrequencyCentroid);

        // signatures row should NOT have frequency_centroid written
        var dbFp = await ReadSignatureFrequencyCentroidAsync("sig-nofp");
        Assert.Null(dbFp);
    }

    // ============================================================
    // Compaction with frequency fingerprints present
    // ============================================================

    [Fact]
    public async Task CompactSignatureSessions_WithFingerprints_FrequencyCentroidIsAveraged()
    {
        await SeedSignatureAsync("sig-fp");

        // 4 sessions with known fingerprints + 2 to keep
        var fp1 = new float[] { 0.8f, 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        var fp2 = new float[] { 0.4f, 0.3f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };

        // 4 sessions to be compacted (2 with fp1, 2 with fp2), plus 2 to keep
        await SeedSessionAsync("sig-fp", fingerprint: fp1);
        await SeedSessionAsync("sig-fp", fingerprint: fp1);
        await SeedSessionAsync("sig-fp", fingerprint: fp2);
        await SeedSessionAsync("sig-fp", fingerprint: fp2);
        await SeedSessionAsync("sig-fp", hasFp: false); // kept
        await SeedSessionAsync("sig-fp", hasFp: false); // kept

        var result = await _store.CompactSignatureSessionsAsync("sig-fp", keepCount: 2);

        Assert.Equal(4, result.CompactedCount);
        Assert.NotNull(result.FrequencyCentroid);

        // Average of fp1,fp1,fp2,fp2: dim0 = (0.8+0.8+0.4+0.4)/4 = 0.6
        Assert.Equal(0.6f, result.FrequencyCentroid![0], 4);
        // dim1 = (0.1+0.1+0.3+0.3)/4 = 0.2
        Assert.Equal(0.2f, result.FrequencyCentroid[1], 4);
    }

    [Fact]
    public async Task CompactSignatureSessions_WithFingerprints_PersistsToDatabase()
    {
        await SeedSignatureAsync("sig-persist");

        var fp = new float[] { 0.9f, 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        for (var i = 0; i < 4; i++)
            await SeedSessionAsync("sig-persist", fingerprint: fp);
        // 2 sessions to keep
        await SeedSessionAsync("sig-persist", hasFp: false);
        await SeedSessionAsync("sig-persist", hasFp: false);

        var result = await _store.CompactSignatureSessionsAsync("sig-persist", keepCount: 2);

        Assert.NotNull(result.FrequencyCentroid);

        // The BLOB column on signatures should match the centroid
        var dbFp = await ReadSignatureFrequencyCentroidAsync("sig-persist");
        Assert.NotNull(dbFp);
        Assert.Equal(8, dbFp!.Length);
        Assert.Equal(0.9f, dbFp[0], 4);
        Assert.Equal(0.1f, dbFp[1], 4);
    }

    [Fact]
    public async Task CompactSignatureSessions_MixedFpAndNoFp_AveragesOnlyFpSessions()
    {
        await SeedSignatureAsync("sig-mixed");

        var fp = new float[] { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        await SeedSessionAsync("sig-mixed", fingerprint: fp);     // compacted, has fp
        await SeedSessionAsync("sig-mixed", hasFp: false);        // compacted, no fp
        await SeedSessionAsync("sig-mixed", hasFp: false);        // compacted, no fp
        await SeedSessionAsync("sig-mixed", hasFp: false);        // kept
        await SeedSessionAsync("sig-mixed", hasFp: false);        // kept

        var result = await _store.CompactSignatureSessionsAsync("sig-mixed", keepCount: 2);

        Assert.Equal(3, result.CompactedCount);
        // Only 1 session had a fingerprint — centroid equals that single fp
        Assert.NotNull(result.FrequencyCentroid);
        Assert.Equal(1.0f, result.FrequencyCentroid![0], 4);
    }

    // ============================================================
    // BehavioralCentroid is still returned correctly
    // ============================================================

    [Fact]
    public async Task CompactSignatureSessions_ReturnsBehavioralCentroid()
    {
        await SeedSignatureAsync("sig-vec");

        // All sessions with the same unit vector so centroid == unit vector
        for (var i = 0; i < 5; i++)
            await SeedSessionAsync("sig-vec", hasFp: false);
        await SeedSessionAsync("sig-vec", hasFp: false); // kept

        var result = await _store.CompactSignatureSessionsAsync("sig-vec", keepCount: 1);

        Assert.Equal(5, result.CompactedCount);
        Assert.NotNull(result.BehavioralCentroid);
        Assert.Equal(VecDims, result.BehavioralCentroid!.Length);
    }

    // ============================================================
    // Remaining session count after compaction
    // ============================================================

    [Fact]
    public async Task CompactSignatureSessions_DeletesOldRows_KeepsRecent()
    {
        await SeedSignatureAsync("sig-del");

        for (var i = 0; i < 6; i++)
            await SeedSessionAsync("sig-del", hasFp: false);

        await _store.CompactSignatureSessionsAsync("sig-del", keepCount: 2);

        var remaining = await CountSessionsAsync("sig-del");
        Assert.Equal(2, remaining);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task SeedSignatureAsync(string sig)
    {
        await _store.UpsertSignatureAsync(new PersistedSignature
        {
            SignatureId = sig,
            SessionCount = 1,
            TotalRequestCount = 10,
            FirstSeen = DateTime.UtcNow.AddHours(-2),
            LastSeen = DateTime.UtcNow,
            IsBot = false,
            BotProbability = 0.1,
            Confidence = 0.8,
            RiskBand = "Low"
        });
    }

    private int _sessionSeq;

    private async Task SeedSessionAsync(string sig, bool hasFp = true, float[]? fingerprint = null)
    {
        var seq = Interlocked.Increment(ref _sessionSeq);
        var vec = MakeUnitVector();
        var vecBytes = SqliteSessionStore.SerializeVector(vec);

        float[]? fp = fingerprint ?? (hasFp ? new float[8] { 0.5f, 0.1f, 0f, 0f, 0f, 0f, 0f, 0f } : null);
        byte[]? fpBytes = fp != null ? SqliteSessionStore.SerializeVector(fp) : null;

        await _store.AddSessionAsync(new PersistedSession
        {
            Signature = sig,
            StartedAt = DateTime.UtcNow.AddMinutes(-60 + seq),
            EndedAt = DateTime.UtcNow.AddMinutes(-59 + seq),
            RequestCount = 5,
            Vector = vecBytes,
            Maturity = 0.5f,
            DominantState = "PageView",
            IsBot = false,
            AvgBotProbability = 0.1,
            AvgConfidence = 0.8,
            RiskBand = "Low",
            FrequencyFingerprintBlob = fpBytes
        });
    }

    private static float[] MakeUnitVector()
    {
        var v = new float[VecDims];
        v[0] = 1.0f;
        return v;
    }

    private async Task<float[]?> ReadSignatureFrequencyCentroidAsync(string sig)
    {
        var connStr = GetConnectionString();
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT frequency_centroid FROM signatures WHERE signature_id = @sig";
        cmd.Parameters.AddWithValue("@sig", sig);
        var raw = await cmd.ExecuteScalarAsync();
        if (raw is null or DBNull) return null;
        return SqliteSessionStore.DeserializeVector((byte[])raw);
    }

    private async Task<int> CountSessionsAsync(string sig)
    {
        var connStr = GetConnectionString();
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE signature = @sig";
        cmd.Parameters.AddWithValue("@sig", sig);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private string GetConnectionString()
    {
        // Mirrors SqliteSessionStore's internal path construction:
        // basePath = GetDirectoryName(DatabasePath), dbPath = basePath/sessions.db
        var dbPath = Path.Combine(_dbDir, "sessions.db");
        return $"Data Source={dbPath};Cache=Shared";
    }
}
