using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Behaviour-preservation tests for <see cref="SessionCompactionGuardian"/>
///     (Phase 2 of the old VectorCompactionService).
///
///     Uses a real <see cref="SqliteDetectionArchive"/> (in-process, temp file)
///     so the compaction SQL actually runs. The contract is:
///     <list type="bullet">
///         <item>A signature with more sessions than <c>MaxSessionsPerSignature</c>
///             gets its oldest sessions folded into the root_vector centroid;
///             only the most recent sessions remain at full resolution.</item>
///         <item>The count of signatures compacted is returned.</item>
///         <item>The report carries <c>Status="compacted"</c> when work was done
///             and <c>Status="ok"</c> when nothing overflowed.</item>
///         <item>The guardian honours the <see cref="IGuardian"/> contract:
///             name, category, interval, and enabled flag.</item>
///     </list>
/// </summary>
public sealed class SessionCompactionGuardianTests : IAsyncLifetime
{
    private SqliteDetectionArchive _store = null!;
    private string _dbDir = null!;
    private static readonly int VecDims = SessionVectorizer.Dimensions;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"scg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var dbFilePath = Path.Combine(_dbDir, "botdetection.db");

        var opts = Options.Create(new BotDetectionOptions { DatabasePath = dbFilePath });
        _store = new SqliteDetectionArchive(NullLogger<SqliteDetectionArchive>.Instance, opts);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- IGuardian contract ----

    [Fact]
    public void Is_a_data_category_guardian_named_SessionCompaction()
    {
        var sut = BuildGuardian();

        sut.Should().BeAssignableTo<IGuardian>();
        sut.Name.Should().Be("SessionCompaction");
        sut.Category.Should().Be(GuardianCategory.Data);
    }

    [Fact]
    public void Interval_defaults_to_CompactionInterval_from_options()
    {
        var opts = DefaultOpts();
        var sut = BuildGuardian(opts: opts);

        sut.Interval.Should().Be(opts.Value.Retention.CompactionInterval);
    }

    [Fact]
    public void Interval_can_be_overridden_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:SessionCompaction:Interval"] = "03:00:00"
            })
            .Build();

        var sut = BuildGuardian(config: config);

        sut.Interval.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Enabled_defaults_to_true()
    {
        var sut = BuildGuardian();
        sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_can_be_set_false_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:SessionCompaction:Enabled"] = "false"
            })
            .Build();

        var sut = BuildGuardian(config: config);
        sut.Enabled.Should().BeFalse();
    }

    // ---- Real compaction behaviour ----

    [Fact]
    public async Task GuardAsync_returns_ok_when_no_signature_overflows()
    {
        // Only 1 session -- below any sane MaxSessionsPerSignature
        await SeedSignatureAsync("sig-under");
        await SeedSessionAsync("sig-under");

        var sut = BuildGuardian(maxSessions: 5);
        var report = await sut.GuardAsync();

        report.GuardianName.Should().Be("SessionCompaction");
        report.Category.Should().Be(GuardianCategory.Data);
        report.Status.Should().Be("ok");
        report.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GuardAsync_compacts_overflow_sessions_and_reports_the_count()
    {
        // Seed a signature with MaxSessionsPerSignature + 3 sessions so it overflows.
        const int maxSessions = 3;
        const int totalSessions = maxSessions + 3; // 6 total, 3 over the limit
        await SeedSignatureAsync("sig-overflow");
        for (var i = 0; i < totalSessions; i++)
            await SeedSessionAsync("sig-overflow");

        var sut = BuildGuardian(maxSessions: maxSessions);
        var report = await sut.GuardAsync();

        report.Status.Should().Be("compacted");
        report.Details.Should().Contain("1"); // 1 signature compacted
    }

    [Fact]
    public async Task GuardAsync_leaves_only_MaxSessionsPerSignature_rows_after_compaction()
    {
        // Seed two signatures: one that overflows, one that doesn't.
        const int maxSessions = 2;

        await SeedSignatureAsync("sig-over");
        for (var i = 0; i < maxSessions + 4; i++) // 6 sessions - over limit
            await SeedSessionAsync("sig-over");

        await SeedSignatureAsync("sig-ok");
        for (var i = 0; i < maxSessions; i++) // 2 sessions - at limit
            await SeedSessionAsync("sig-ok");

        var sut = BuildGuardian(maxSessions: maxSessions);
        await sut.GuardAsync();

        // The overflowing signature is trimmed to maxSessions full-resolution rows.
        var remainingOver = await CountSessionsAsync("sig-over");
        remainingOver.Should().Be(maxSessions,
            "only the most recent {0} sessions should remain at full resolution", maxSessions);

        // The under-limit signature is untouched.
        var remainingOk = await CountSessionsAsync("sig-ok");
        remainingOk.Should().Be(maxSessions, "under-limit signatures must not be touched");
    }

    [Fact]
    public async Task GuardAsync_updates_root_vector_on_signature_after_compaction()
    {
        const int maxSessions = 2;
        await SeedSignatureAsync("sig-centroid");
        for (var i = 0; i < maxSessions + 3; i++)
            await SeedSessionAsync("sig-centroid", vectorValue: (float)(i + 1) * 0.1f);

        var sut = BuildGuardian(maxSessions: maxSessions);
        await sut.GuardAsync();

        // root_vector must have been written: read it back via SQLite.
        var rootVector = await ReadRootVectorAsync("sig-centroid");
        rootVector.Should().NotBeNull("compaction must produce a behavioural centroid on the signature row");
        rootVector!.Length.Should().Be(VecDims, "centroid dimensions must match session vector dimensions");
    }

    [Fact]
    public async Task GuardAsync_compacts_multiple_overflowing_signatures_and_reports_total_count()
    {
        const int maxSessions = 2;

        await SeedSignatureAsync("sig-a");
        for (var i = 0; i < maxSessions + 2; i++)
            await SeedSessionAsync("sig-a");

        await SeedSignatureAsync("sig-b");
        for (var i = 0; i < maxSessions + 2; i++)
            await SeedSessionAsync("sig-b");

        var sut = BuildGuardian(maxSessions: maxSessions);
        var report = await sut.GuardAsync();

        report.Status.Should().Be("compacted");
        // Both signatures were compacted, so the details mention "2".
        report.Details.Should().Contain("2");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static IOptions<BotDetectionOptions> DefaultOpts(int maxSessions = 10) =>
        Options.Create(new BotDetectionOptions
        {
            Retention = { MaxSessionsPerSignature = maxSessions }
        });

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private SessionCompactionGuardian BuildGuardian(
        int maxSessions = 10,
        IOptions<BotDetectionOptions>? opts = null,
        IConfiguration? config = null) =>
        new(
            _store,
            opts ?? DefaultOpts(maxSessions),
            config ?? EmptyConfig(),
            NullLogger<SessionCompactionGuardian>.Instance);

    private async Task SeedSignatureAsync(string sig)
    {
        await _store.UpsertSignatureAsync(RequestScope.Unknown, new PersistedSignature
        {
            SignatureId = sig,
            SessionCount = 1,
            TotalRequestCount = 5,
            FirstSeen = DateTime.UtcNow.AddHours(-2),
            LastSeen = DateTime.UtcNow,
            IsBot = false,
            BotProbability = 0.1,
            Confidence = 0.7,
            RiskBand = "Low"
        });
    }

    private int _sessionSeq;

    private async Task SeedSessionAsync(string sig, float vectorValue = 1.0f)
    {
        var seq = Interlocked.Increment(ref _sessionSeq);
        var vec = new float[VecDims];
        vec[0] = vectorValue;

        await _store.AddSessionAsync(RequestScope.Unknown, new PersistedSession
        {
            Signature = sig,
            StartedAt = DateTime.UtcNow.AddMinutes(-120 + seq),
            EndedAt = DateTime.UtcNow.AddMinutes(-119 + seq),
            RequestCount = 5,
            Vector = SqliteDetectionArchive.SerializeVector(vec),
            Maturity = 0.5f,
            DominantState = "PageView",
            IsBot = false,
            AvgBotProbability = 0.1,
            AvgConfidence = 0.7,
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

    private async Task<float[]?> ReadRootVectorAsync(string sig)
    {
        var dbPath = Path.Combine(_dbDir, "sessions.db");
        await using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT root_vector FROM signatures WHERE signature_id = @sig";
        cmd.Parameters.AddWithValue("@sig", sig);
        var raw = await cmd.ExecuteScalarAsync();
        if (raw is DBNull or null) return null;
        return SqliteDetectionArchive.DeserializeVector((byte[])raw);
    }
}
