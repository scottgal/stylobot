using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Tests for the EWMA upsert behaviour of the signatures table.
///     The prior implementation used MAX(bot_probability, observation), which pinned a
///     signature at its highest historical score forever. EWMA lets a high prior decay
///     toward repeated benign observations, matching how a real actor's risk evolves.
/// </summary>
public class SignatureUpsertEwmaTests : IAsyncLifetime
{
    private SqliteDetectionArchive _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        // SqliteDetectionArchive takes the parent directory of DatabasePath and writes sessions.db inside it.
        _dbDir = Path.Combine(Path.GetTempPath(), $"sig-ewma-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var dbFilePath = Path.Combine(_dbDir, "botdetection.db");

        var opts = Options.Create(new BotDetectionOptions { DatabasePath = dbFilePath });
        _store = new SqliteDetectionArchive(NullLogger<SqliteDetectionArchive>.Instance, opts);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
    }

    private static PersistedSignature MakeSignature(
        string id,
        double botProbability,
        double confidence = 0.6,
        int sessionCount = 1,
        int totalRequests = 1)
    {
        var now = DateTime.UtcNow;
        return new PersistedSignature
        {
            SignatureId = id,
            SessionCount = sessionCount,
            TotalRequestCount = totalRequests,
            FirstSeen = now,
            LastSeen = now,
            IsBot = botProbability >= 0.5,
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = botProbability >= 0.8 ? "High" : botProbability >= 0.5 ? "Medium" : "Low"
        };
    }

    [Fact]
    public async Task UpsertSignature_FirstObservation_StoresLiteralValue()
    {
        await _store.UpsertSignatureAsync(RequestScope.Unknown, MakeSignature("sig-A", botProbability: 0.8));

        var stored = await _store.GetSignatureAsync("sig-A");
        Assert.NotNull(stored);
        Assert.Equal(0.8, stored!.BotProbability, precision: 2);
    }

    [Fact]
    public async Task UpsertSignature_BenignObservation_DecaysHighPrior()
    {
        // EWMA must replace MAX. A 0.95 prior followed by ten 0.05 observations
        // must NOT remain at 0.95.
        await _store.UpsertSignatureAsync(RequestScope.Unknown, MakeSignature("sig-B", botProbability: 0.95));
        for (var i = 0; i < 10; i++)
            await _store.UpsertSignatureAsync(RequestScope.Unknown, MakeSignature("sig-B", botProbability: 0.05));

        var stored = await _store.GetSignatureAsync("sig-B");
        Assert.NotNull(stored);
        Assert.True(stored!.BotProbability < 0.5,
            $"After ten benign observations the EWMA should drop below 0.5, got {stored.BotProbability:F3}");
    }

    [Fact]
    public async Task UpsertSignature_RecordsLastUpdatedUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        await _store.UpsertSignatureAsync(RequestScope.Unknown, MakeSignature("sig-C", botProbability: 0.5));
        var after = DateTime.UtcNow.AddSeconds(1);

        var stored = await _store.GetSignatureAsync("sig-C");
        Assert.NotNull(stored);
        Assert.NotNull(stored!.LastUpdatedUtc);
        Assert.InRange(stored.LastUpdatedUtc!.Value, before, after);
    }
}
