using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Cross-signature cap enforcement primitives on <see cref="SqliteSessionStore"/>
///     (data guardian, step 3b): the signature count, the oldest-first priority
///     candidate scan, and the cascading delete that the guardian uses to evict
///     the lowest-value signatures when the store overflows its cap.
/// </summary>
public class SqliteSignatureEvictionTests : IAsyncLifetime
{
    private SqliteSessionStore _store = null!;
    private string _dbDir = null!;
    private static readonly int VecDims = SessionVectorizer.Dimensions;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"eviction-test-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task GetSignatureCountAsync_counts_distinct_signatures()
    {
        Assert.Equal(0, await _store.GetSignatureCountAsync());

        await SeedSignatureAsync("sig-a");
        await SeedSignatureAsync("sig-b");
        await SeedSignatureAsync("sig-c");

        Assert.Equal(3, await _store.GetSignatureCountAsync());
    }

    [Fact]
    public async Task GetAllSignaturePriorityInfoAsync_returns_oldest_first_within_limit()
    {
        var now = DateTime.UtcNow;
        await SeedSignatureAsync("newest", lastSeen: now);
        await SeedSignatureAsync("middle", lastSeen: now.AddDays(-5));
        await SeedSignatureAsync("oldest", lastSeen: now.AddDays(-30));

        var top2 = await _store.GetAllSignaturePriorityInfoAsync(limit: 2);

        // Oldest-first ordering, limit respected: "newest" is excluded.
        Assert.Equal(2, top2.Count);
        Assert.Equal("oldest", top2[0].Signature);
        Assert.Equal("middle", top2[1].Signature);
    }

    [Fact]
    public async Task GetAllSignaturePriorityInfoAsync_carries_score_inputs()
    {
        await SeedSignatureAsync("risky", botProb: 0.92, riskBand: "VeryHigh", isBot: true);

        var info = (await _store.GetAllSignaturePriorityInfoAsync(limit: 10)).Single();

        Assert.Equal("risky", info.Signature);
        Assert.Equal(0.92, info.BotProbability, 4);
        Assert.Equal("VeryHigh", info.RiskBand);
        Assert.True(info.IsBot);
    }

    [Fact]
    public async Task DeleteSignaturesAsync_removes_signatures_and_their_sessions()
    {
        await SeedSignatureAsync("keep");
        await SeedSessionAsync("keep");
        await SeedSessionAsync("keep");

        await SeedSignatureAsync("drop");
        await SeedSessionAsync("drop");
        await SeedSessionAsync("drop");
        await SeedSessionAsync("drop");

        var deleted = await _store.DeleteSignaturesAsync(new[] { "drop" });

        Assert.Equal(1, deleted);
        Assert.Equal(1, await _store.GetSignatureCountAsync());
        Assert.Equal(0, await CountSessionsAsync("drop"));   // sessions cascaded away
        Assert.Equal(2, await CountSessionsAsync("keep"));   // untouched
    }

    [Fact]
    public async Task DeleteSignaturesAsync_empty_is_a_noop()
    {
        await SeedSignatureAsync("only");

        var deleted = await _store.DeleteSignaturesAsync(Array.Empty<string>());

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _store.GetSignatureCountAsync());
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task SeedSignatureAsync(
        string sig,
        double botProb = 0.1,
        string riskBand = "Low",
        bool isBot = false,
        DateTime? lastSeen = null)
    {
        await _store.UpsertSignatureAsync(RequestScope.Unknown, new PersistedSignature
        {
            SignatureId = sig,
            SessionCount = 1,
            TotalRequestCount = 10,
            FirstSeen = (lastSeen ?? DateTime.UtcNow).AddHours(-2),
            LastSeen = lastSeen ?? DateTime.UtcNow,
            IsBot = isBot,
            BotProbability = botProb,
            Confidence = 0.8,
            RiskBand = riskBand
        });
    }

    private int _sessionSeq;

    private async Task SeedSessionAsync(string sig)
    {
        var seq = Interlocked.Increment(ref _sessionSeq);
        var vec = new float[VecDims];
        vec[0] = 1.0f;

        await _store.AddSessionAsync(RequestScope.Unknown, new PersistedSession
        {
            Signature = sig,
            StartedAt = DateTime.UtcNow.AddMinutes(-60 + seq),
            EndedAt = DateTime.UtcNow.AddMinutes(-59 + seq),
            RequestCount = 5,
            Vector = SqliteSessionStore.SerializeVector(vec),
            Maturity = 0.5f,
            DominantState = "PageView",
            IsBot = false,
            AvgBotProbability = 0.1,
            AvgConfidence = 0.8,
            RiskBand = "Low"
        });
    }

    private async Task<int> CountSessionsAsync(string sig)
    {
        var dbPath = Path.Combine(_dbDir, "sessions.db");
        await using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE signature = @sig";
        cmd.Parameters.AddWithValue("@sig", sig);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
