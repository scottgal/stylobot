using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Sessions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     End-to-end: session upserted → TTL evicts → SessionStore raises
///     finalizing → SessionEchoAtom drains signal → DetectionArchiveEchoStore
///     delegates to IDetectionArchive.AddEchoAsync → SqliteDetectionArchive
///     writes a row into <c>session_echoes</c>. Verifies the whole signal-
///     driven persistence path lands in the database on real infra.
/// </summary>
public sealed class SessionEchoArchiveIntegrationTests : IAsyncLifetime
{
    private const string SiteId = "site-e2e";
    private const string FingerprintId = "fp-e2e";

    private string _dbDir = null!;
    private SqliteDetectionArchive _archive = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "sb-echo-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);

        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "dummy.db"),
        });
        _archive = new SqliteDetectionArchive(NullLogger<SqliteDetectionArchive>.Instance, opts);
        await _archive.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _archive.DisposeAsync();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best-effort */ }
    }

    private SessionStore NewStore()
    {
        var options = Options.Create(new SessionStoreOptions
        {
            Ttl = TimeSpan.FromMilliseconds(10),
            MinTtlUnderPressure = TimeSpan.FromMilliseconds(10),
            MaxLifetime = TimeSpan.FromHours(1),
            CleanupInterval = TimeSpan.FromMilliseconds(20),
            FinalizeDeadline = TimeSpan.FromSeconds(3),
            MinFinalizeDeadlineUnderPressure = TimeSpan.FromSeconds(3),
            ExpectedAckCount = 1,
            EmitFinalizingSignal = true,
            AckPollInterval = TimeSpan.FromMilliseconds(5),
        });
        return new SessionStore(options, NullLogger<SessionStore>.Instance);
    }

    private static SessionSample NewSample(int statusCode = 404, bool honeypot = false, double botProb = 0.72) => new()
    {
        FingerprintId = FingerprintId,
        SiteId = SiteId,
        Timestamp = DateTimeOffset.UtcNow.AddSeconds(-30),
        BotProbability = botProb,
        Confidence = 0.6,
        StatusCode = statusCode,
        FromUpstream = true,
        Honeypot = honeypot,
    };

    [Fact]
    public async Task Session_eviction_writes_echo_row_through_the_archive()
    {
        using var store = NewStore();
        var echoStore = new DetectionArchiveEchoStore(_archive);
        using var atom = new SessionEchoAtom(store, echoStore, NullLogger<SessionEchoAtom>.Instance);

        // Escalator equivalent: publish a sample. TTL is 10ms, cleanup fires
        // every 20ms — the sample is immediately eligible for eviction because
        // its Timestamp is 30s in the past.
        store.Upsert(NewSample(statusCode: 404, honeypot: true, botProb: 0.72));

        // Wait for the row to land in SQLite. Bounded by the finalize deadline
        // (3s) + drainer processing time.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var count = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            count = await CountEchoRowsAsync();
            if (count > 0) break;
            await Task.Delay(50);
        }

        count.Should().Be(1,
            "one session eviction must produce exactly one echo row in the archive");

        var row = await ReadOneEchoAsync();
        row.FingerprintId.Should().Be(FingerprintId);
        row.SiteId.Should().Be(SiteId);
        row.HoneypotHits.Should().Be(1,
            "the archive column must preserve the honeypot hit count from the aggregate");
        row.MeanBotProbability.Should().BeApproximately(0.72, 0.001);
        row.FinalizeReason.Should().Be(SessionFinalizeReason.Ttl,
            "TTL-driven eviction must be recorded as such so operators can audit shed decisions");
        row.UpstreamStatusCountsJson.Should().Contain("404",
            "upstream status distribution must survive the JSON serialization round-trip");
    }

    private async Task<int> CountEchoRowsAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dbDir, "sessions.db")};Cache=Shared");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session_echoes WHERE fingerprint_id = @fp";
        cmd.Parameters.AddWithValue("@fp", FingerprintId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<EchoRow> ReadOneEchoAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dbDir, "sessions.db")};Cache=Shared");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, site_id, honeypot_hits, mean_bot_probability,
                   finalize_reason, upstream_status_counts
            FROM session_echoes
            WHERE fingerprint_id = @fp
            ORDER BY id DESC
            LIMIT 1
        """;
        cmd.Parameters.AddWithValue("@fp", FingerprintId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new EchoRow(
            FingerprintId: reader.GetString(0),
            SiteId: reader.GetString(1),
            HoneypotHits: reader.GetInt32(2),
            MeanBotProbability: reader.GetDouble(3),
            FinalizeReason: (SessionFinalizeReason)reader.GetInt32(4),
            UpstreamStatusCountsJson: reader.GetString(5));
    }

    private sealed record EchoRow(
        string FingerprintId,
        string SiteId,
        int HoneypotHits,
        double MeanBotProbability,
        SessionFinalizeReason FinalizeReason,
        string UpstreamStatusCountsJson);
}
