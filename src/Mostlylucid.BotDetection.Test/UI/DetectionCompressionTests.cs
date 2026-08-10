using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Write-time importance score math (DetectionImportance) — the compression
///     fold's ordering key. Blend weights default 0.5/0.3/0.2 (bot/threat/action),
///     floor default 0.4; all overridable via TemporalStoreOptions.
/// </summary>
public sealed class DetectionImportanceTests
{
    private static TemporalStoreOptions Defaults() => new();

    [Fact]
    public void Plain_low_score_request_lands_near_zero()
    {
        DetectionImportance.ComputeWeight(0.1, null, "allow", Defaults())
            .Should().BeApproximately(0.05, 0.001);
    }

    [Fact]
    public void High_bot_probability_raises_importance()
    {
        DetectionImportance.ComputeWeight(0.9, null, null, Defaults())
            .Should().BeApproximately(0.45, 0.001);
    }

    [Fact]
    public void Threat_score_contributes_through_the_normalizer()
    {
        // Threat scores are 0..1 normalized; normalizer default 1.0.
        DetectionImportance.ComputeWeight(0.0, 1.0, null, Defaults())
            .Should().BeApproximately(0.3, 0.001);
    }

    [Fact]
    public void Enforcement_actions_rank_block_above_throttle_above_none()
    {
        var block = DetectionImportance.ComputeWeight(0.0, null, "block", Defaults());
        var challenge = DetectionImportance.ComputeWeight(0.0, null, "challenge", Defaults());
        var honeypot = DetectionImportance.ComputeWeight(0.0, null, "honeypot-response", Defaults());
        var throttle = DetectionImportance.ComputeWeight(0.0, null, "throttle-stealth", Defaults());
        var rate = DetectionImportance.ComputeWeight(0.0, null, "rate-limit", Defaults());
        var none = DetectionImportance.ComputeWeight(0.0, null, "allow", Defaults());

        block.Should().BeApproximately(0.2, 0.0001);
        challenge.Should().BeApproximately(0.16, 0.0001);
        honeypot.Should().BeApproximately(0.14, 0.0001);
        throttle.Should().BeApproximately(0.12, 0.0001);
        rate.Should().BeApproximately(0.08, 0.0001);
        none.Should().BeApproximately(0.0, 0.0001);
    }

    [Fact]
    public void Weight_is_clamped_to_one()
    {
        // Bot 1.0 + threat 1.0 + block = 0.5 + 0.3 + 0.2 = 1.0; a >1 input clamps.
        DetectionImportance.ComputeWeight(1.0, 2.0, "block", Defaults()).Should().Be(1.0);
    }

    [Fact]
    public void Operator_can_tilt_the_blend()
    {
        var opts = new TemporalStoreOptions
        {
            BotScoreWeight = 0.8,
            ThreatScoreWeight = 0.1,
            ActionWeight = 0.1
        };
        DetectionImportance.ComputeWeight(0.5, null, null, opts)
            .Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public void Enforcement_keyword_set_matches_the_fusion_predicate_keywords()
    {
        // The fold's SQL fusion exemption mirrors IsEnforcementAction. If the
        // keyword sets drift, enforcement rows could start fusing silently —
        // this pins them together.
        foreach (var action in new[] { "block", "block-hard", "challenge", "honeypot-response",
                                       "simulation-pack", "throttle-stealth", "throttle-gentle", "rate-limit" })
        {
            DetectionImportance.IsEnforcementAction(action).Should().BeTrue(action);
        }
        foreach (var action in new[] { "allow", "observe", null, "", "shed" })
        {
            DetectionImportance.IsEnforcementAction(action).Should().BeFalse(action ?? "null");
        }
    }
}

/// <summary>
///     The compression fold against a real SQLite store. Two tiers:
///     low-importance rows past the hot window FUSE into one summary row per
///     (signature, hour, domain, country, bot_type) — the row-count reduction —
///     while enforcement/threat rows and important rows keep their own rows
///     (detail-nulled at full absorption). Counts must be IDENTICAL before vs
///     after folding — the old code could only null detail columns, never
///     reduce row count; these tests prove the new behavior keeps every count
///     exact while collapsing the table.
/// </summary>
public sealed class SqliteCompressionFoldTests : IDisposable
{
    private static readonly TemporalStoreOptions Defaults = new();
    private readonly string _tempDir;
    private readonly SqliteDashboardEventStore _store;

    public SqliteCompressionFoldTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-fold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var detectionOptions = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db")
        });
        var dashboardOptions = Options.Create(new StyloBotDashboardOptions
        {
            DetectionRetention = TimeSpan.FromDays(30)
        });
        _store = new SqliteDashboardEventStore(
            NullLogger<SqliteDashboardEventStore>.Instance, detectionOptions, dashboardOptions);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static DashboardDetectionEvent Event(
        string signature,
        DateTime timestamp,
        double botProbability,
        string? action = null,
        double? threatScore = null,
        string? domain = "example.com",
        string? country = "GB")
        => new()
        {
            RequestId = Guid.NewGuid().ToString(),
            Timestamp = timestamp,
            IsBot = botProbability >= 0.5,
            BotProbability = botProbability,
            Confidence = 0.9,
            RiskBand = botProbability >= 0.5 ? "High" : "Low",
            Method = "GET",
            Path = "/probe",
            StatusCode = 200,
            ProcessingTimeMs = 5.0,
            ResponseBytes = 4096,
            UserAgentRaw = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            Action = action,
            ThreatScore = threatScore,
            PrimarySignature = signature,
            CountryCode = country,
            RiskJustification = "seed justification",
            ReferrerHost = "referrer.example",
            UaDeviceClass = "desktop",
            Domain = domain
        };

    private async Task<int> RawRowCountAsync()
    {
        await using var conn = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "dashboard.db")}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM detections";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<int> RawFusedCountAsync()
    {
        await using var conn = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "dashboard.db")}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM detections WHERE fused = 1";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Schema_has_importance_weight_and_fusion_columns()
    {
        await _store.GetDetectionsAsync(); // triggers init

        await using var conn = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "dashboard.db")}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(detections);";
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        columns.Should().Contain("importance_weight");
        columns.Should().Contain("fused");
        columns.Should().Contain("hit_count");
        columns.Should().Contain("bot_count");
        columns.Should().Contain("bytes_sum");
        columns.Should().Contain("ms_sum");
        columns.Should().Contain("ms_max");
    }

    [Fact]
    public async Task Old_behavior_rows_stayed_rows_until_retention_new_fusion_collapses_them()
    {
        // THE old-vs-new regression: 60 low-importance rows of the same
        // (signature, hour) — what the write throttle produces for one
        // signature in an hour of 1-row-per-minute persistence. The old code
        // (detail-null only) left all 60 rows in the table; fusion collapses
        // them into ONE summary row.
        var now = DateTime.UtcNow;
        for (var i = 0; i < 60; i++)
        {
            await _store.AddDetectionAsync(Event(
                "sig-scanner", now.AddHours(-6), 0.1, "allow", null));
        }

        (await RawRowCountAsync()).Should().Be(60);

        var folded = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);
        folded.Should().Be(60);

        // New behavior: 60 rows -> 1 fused summary row.
        (await RawRowCountAsync()).Should().Be(1);
        (await RawFusedCountAsync()).Should().Be(1);

        // The fused row kept every count exact (the old code couldn't do this
        // without keeping all 60 rows).
        var summary = await _store.GetSummaryAsync(now.AddHours(-24), now);
        summary.TotalRequests.Should().Be(60);
        summary.BotRequests.Should().Be(0);
        summary.HumanRequests.Should().Be(60);
        summary.BytesOut.Should().Be(60L * 4096);
        summary.AverageProcessingTimeMs.Should().BeApproximately(5.0, 0.001);
        summary.MaxProcessingTimeMs.Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public async Task Fusion_keeps_summary_timeseries_topbots_domain_and_country_exact()
    {
        var now = DateTime.UtcNow;
        // A mixed group: 40 low-importance bots (prob 0.7 -> weight 0.35 < 0.4
        // floor, still "bot" at the 0.5 read floor) + 20 low-importance humans,
        // same signature/hour/domain/country, plus one important block row and
        // one young row that must both survive as real rows.
        for (var i = 0; i < 40; i++)
            await _store.AddDetectionAsync(Event("sig-mixed", now.AddHours(-6), 0.7, "allow", null));
        for (var i = 0; i < 20; i++)
            await _store.AddDetectionAsync(Event("sig-mixed", now.AddHours(-6), 0.1, "allow", null));
        await _store.AddDetectionAsync(Event("sig-blocked", now.AddHours(-6), 0.9, "block", 0.9));
        await _store.AddDetectionAsync(Event("sig-young", now.AddHours(-1), 0.1, "allow", null));

        var beforeSummary = await _store.GetSummaryAsync(now.AddHours(-24), now);
        var beforeSeries = await _store.GetTimeSeriesAsync(
            now.AddHours(-24), now, TimeSpan.FromHours(1));
        var beforeTop = await _store.GetTopBotsAsync(10, now.AddHours(-24), now);
        var beforeDomain = await _store.GetDomainStatsAsync(now.AddHours(-24), now);
        var beforeCountry = await _store.GetCountryStatsAsync(20, now.AddHours(-24), now);

        await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);

        // 60 fused away + the block row kept + the young row kept = 2 real rows.
        (await RawRowCountAsync()).Should().Be(3);

        var afterSummary = await _store.GetSummaryAsync(now.AddHours(-24), now);
        var afterSeries = await _store.GetTimeSeriesAsync(
            now.AddHours(-24), now, TimeSpan.FromHours(1));
        var afterTop = await _store.GetTopBotsAsync(10, now.AddHours(-24), now);
        var afterDomain = await _store.GetDomainStatsAsync(now.AddHours(-24), now);
        var afterCountry = await _store.GetCountryStatsAsync(20, now.AddHours(-24), now);

        afterSummary.TotalRequests.Should().Be(beforeSummary.TotalRequests);
        afterSummary.BotRequests.Should().Be(beforeSummary.BotRequests);
        afterSummary.HumanRequests.Should().Be(beforeSummary.HumanRequests);
        afterSummary.BytesOut.Should().Be(beforeSummary.BytesOut);
        afterSummary.AverageProcessingTimeMs.Should().Be(beforeSummary.AverageProcessingTimeMs);
        afterSummary.MaxProcessingTimeMs.Should().Be(beforeSummary.MaxProcessingTimeMs);

        afterSeries.Sum(p => p.TotalCount).Should().Be(beforeSeries.Sum(p => p.TotalCount));
        afterSeries.Sum(p => p.BotCount).Should().Be(beforeSeries.Sum(p => p.BotCount));
        afterSeries.Sum(p => p.HumanCount).Should().Be(beforeSeries.Sum(p => p.HumanCount));

        afterTop.Sum(t => t.HitCount).Should().Be(beforeTop.Sum(t => t.HitCount));
        afterDomain.Sum(d => d.Requests).Should().Be(beforeDomain.Sum(d => d.Requests));
        afterDomain.Sum(d => d.Bots).Should().Be(beforeDomain.Sum(d => d.Bots));
        afterCountry.Sum(c => c.TotalCount).Should().Be(beforeCountry.Sum(c => c.TotalCount));
        afterCountry.Sum(c => c.BotCount).Should().Be(beforeCountry.Sum(c => c.BotCount));
    }

    [Fact]
    public async Task Drilldowns_exclude_fused_rows_but_keep_real_rows()
    {
        var now = DateTime.UtcNow;
        await _store.AddDetectionAsync(Event("sig-scanner", now.AddHours(-6), 0.1, "allow", null));
        await _store.AddDetectionAsync(Event("sig-young", now.AddHours(-1), 0.1, "allow", null));

        await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);

        // The drill-down shows only REAL events: the fused scanner row is gone,
        // the young row is still there with its detail.
        var rows = await _store.GetDetectionsAsync();
        rows.Should().ContainSingle();
        rows[0].PrimarySignature.Should().Be("sig-young");
        rows[0].Path.Should().Be("/probe");
        rows[0].UserAgentRaw.Should().NotBeNull();
    }

    [Fact]
    public async Task Young_rows_are_never_folded_or_fused()
    {
        var now = DateTime.UtcNow;
        var young = Event("sig-young", now.AddHours(-1), 0.1, "allow", null);
        await _store.AddDetectionAsync(young);

        var folded = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);
        folded.Should().Be(0);

        var row = (await _store.GetDetectionsAsync()).Single();
        row.Path.Should().Be("/probe");
        row.UserAgentRaw.Should().NotBeNull();
        (await RawFusedCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Important_rows_keep_detail_until_full_absorption()
    {
        var now = DateTime.UtcNow;
        // 7h old: past the hot window AND strictly older than the 6h FAA cutoff.
        var important = Event("sig-important", now.AddHours(-7), 0.9, "block", 1.0);
        await _store.AddDetectionAsync(important);

        // Past the hot window but before full absorption: importance preserves detail.
        var folded = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);
        folded.Should().Be(0);
        (await _store.GetDetectionsAsync()).Single().Path.Should().Be("/probe");

        // Past full absorption: even important rows fold — old rows are their own summary.
        folded = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-6), Defaults.ImportanceFloor, Defaults.FoldBatchSize);
        folded.Should().Be(1);
        (await _store.GetDetectionsAsync()).Single().Path.Should().Be("/");
    }

    [Fact]
    public async Task Enforcement_and_threat_rows_are_exempt_from_fusion()
    {
        var now = DateTime.UtcNow;
        // Low bot probability (low importance) but enforcement action — the
        // audit trail: must NOT fuse, keeps its own row (detail until FAA).
        await _store.AddDetectionAsync(Event("sig-blocked", now.AddHours(-6), 0.1, "block", null));
        // Low bot probability but threat score at/above the fusion ceiling:
        // the evidence feed — must NOT fuse either.
        await _store.AddDetectionAsync(Event("sig-threat", now.AddHours(-6), 0.1, "allow", 0.6));
        // Benign low-importance row — fuses.
        await _store.AddDetectionAsync(Event("sig-benign", now.AddHours(-6), 0.1, "allow", null));

        var folded = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);
        folded.Should().Be(1);

        var rows = await _store.GetDetectionsAsync();
        rows.Should().HaveCount(2); // blocked + threat rows survive as real rows
        rows.Should().NotContain(r => r.PrimarySignature == "sig-benign");
        (await RawFusedCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Fusion_batch_drains_lowest_importance_first_and_merges_split_groups()
    {
        var now = DateTime.UtcNow;
        // Truncate to the hour then add 15 min as a safe baseline — this
        // prevents the per-row AddMinutes offsets from crossing an hour
        // boundary when UtcNow lands near xx:58-x:59 (the fusion key
        // includes the hour bucket, so a boundary cross scatters rows
        // across two hours and silently breaks the expected row count).
        var baseTs = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc)
            .AddHours(-6).AddMinutes(15);
        // Two signatures, same hour: weights sig-lowest ≈ 0.05 (prob 0.1),
        // sig-mid ≈ 0.15 (prob 0.3) — both below the 0.4 floor, both
        // non-enforcement (a rate-limit action would be exempt from fusion).
        // Distinct per-row timestamps make the (weight, timestamp) drain order
        // deterministic across ticks (identical timestamps would tie).
        for (var i = 0; i < 3; i++)
            await _store.AddDetectionAsync(Event("sig-lowest", baseTs.AddMinutes(i), 0.1, "allow", null));
        for (var i = 0; i < 3; i++)
            await _store.AddDetectionAsync(Event("sig-mid", baseTs.AddMinutes(10 + i), 0.3, "allow", null));

        // batch=2 drains lowest-weight first: sig-lowest (0.05) consumes both
        // slots of tick 1, so the sig-mid group is SPLIT across the batch
        // boundary (one member fuses on tick 2, the rest on tick 3).
        var first = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, batchSize: 2);
        first.Should().Be(2);

        var second = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, batchSize: 2);
        second.Should().Be(2);

        var third = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, batchSize: 2);
        third.Should().Be(2);

        // The merge: sig-lowest's tick-2 leftover and sig-mid's tick-2/3
        // leftovers folded into their signature's EXISTING fused rows — one
        // summary row per signature, zero raw rows left.
        (await RawRowCountAsync()).Should().Be(2);
        (await RawFusedCountAsync()).Should().Be(2);

        // Counts exact: 3 + 3 rows preserved in the fused counters.
        var summary = await _store.GetSummaryAsync(now.AddHours(-24), now);
        summary.TotalRequests.Should().Be(6);
    }

    [Fact]
    public async Task Long_period_steady_state_plateaus_instead_of_growing_unbounded()
    {
        // Long-period stability: 30 days of daily volume at throttle rates with
        // the fold + retention running on their cadences. The old code grew
        // ~linearly with (days x daily volume); the fold fuses low-importance
        // rows and retention erases past 30d, so the table must plateau well
        // below the raw accumulation.
        var now = DateTime.UtcNow;
        const int days = 30;
        const int sigsPerDay = 20;
        const int rowsPerSigPerHour = 6; // throttled rate, 6 sampled hours/day

        for (var day = 0; day < days; day++)
        {
            var dayStart = now.AddDays(-(days - day));
            for (var sig = 0; sig < sigsPerDay; sig++)
            {
                for (var hour = 0; hour < 6; hour++)
                {
                    for (var i = 0; i < rowsPerSigPerHour; i++)
                    {
                        await _store.AddDetectionAsync(Event(
                            $"sig-{day}-{sig}",
                            dayStart.AddHours(hour),
                            day % 3 == 0 ? 0.9 : 0.1, // 1/3 important, 2/3 low-importance
                            day % 3 == 0 ? "block" : "allow",
                            null));
                    }
                }
            }

            // Fold + retention on their cadences. Drain fully: the fold's batch
            // (200) is smaller than a day's volume (720), so the fold runs
            // several ticks per day until nothing is left — the steady-state
            // drain the real Tick5m cadence provides.
            while (await _store.FoldAgedDetectionsAsync(
                now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize) > 0)
            {
            }
            await _store.PruneOldDetectionsAsync(now.AddDays(-30));
        }

        // Raw accumulation would be 30 days x 20 sigs x 6h x 6 = 21,600 rows.
        // The fold fuses the low-importance 2/3 of volume into ~1 row per
        // (sig, hour) and retention caps at 30 days: steady state is the kept
        // important rows (10 days x 720) + fused summaries (20 days x 120)
        // ~= 9,600 — a plateau below two-thirds of the raw accumulation,
        // not linear growth.
        var rawCount = await RawRowCountAsync();
        rawCount.Should().BeLessThan(21_600 * 2 / 3);

        // And the counts are still exact for the retained window.
        var summary = await _store.GetSummaryAsync(now.AddDays(-30), now);
        var expected = days * sigsPerDay * 6 * rowsPerSigPerHour;
        summary.TotalRequests.Should().Be(expected);
    }

    [Fact]
    public async Task Default_store_implementation_is_a_no_op_for_other_stores()
    {
        // A non-SQLite store (fake) inherits the interface default: fold is a no-op.
        IDashboardEventStore fake = new NoFoldEventStore();
        var folded = await fake.FoldAgedDetectionsAsync(
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-48), 0.4, 200);
        folded.Should().Be(0);
    }

    private sealed class NoFoldEventStore : IDashboardEventStore
    {
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => Task.CompletedTask;
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => Task.FromResult(signature);
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => Task.FromResult(new List<DashboardDetectionEvent>());
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => Task.FromResult(new List<DashboardSignatureEvent>());
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new DashboardSummary
        {
            Timestamp = DateTime.UtcNow,
            TotalRequests = 0,
            BotRequests = 0,
            HumanRequests = 0,
            UncertainRequests = 0,
            RiskBandCounts = new Dictionary<string, int>(),
            TopBotTypes = new Dictionary<string, int>(),
            TopActions = new Dictionary<string, int>(),
            UniqueSignatures = 0
        });
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<DashboardTimeSeriesPoint>());
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<DashboardTopBotEntry>());
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<DashboardCountryStats>());
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardCountryDetail?>(null);
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<DashboardEndpointStats>());
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => Task.FromResult(new List<SignatureEndpointStats>());
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardEndpointDetail?>(null);
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<ThreatEntry>());
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => Task.FromResult(new List<UserAgentSearchResult>());
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => Task.FromResult(new List<UserAgentVersionBucket>());
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => Task.FromResult(new List<HoneypotHitRow>());
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => Task.FromResult(new InvestigationResult
        {
            Summary = new InvestigationSummary()
        });
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());
    }
}

/// <summary>
///     The fold's tick driver (DetectionCompressionFold) and the retention
///     pruner's tick (DetectionRetentionPruner): both subscribe to the schedule
///     coordinator, self-disable without one, and are gated by their config.
/// </summary>
public sealed class DetectionCompressionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDashboardEventStore _store;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public DetectionCompressionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-foldsvc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var detectionOptions = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db")
        });
        var dashboardOptions = Options.Create(new StyloBotDashboardOptions
        {
            DetectionRetention = TimeSpan.FromDays(30)
        });
        _store = new SqliteDashboardEventStore(
            NullLogger<SqliteDashboardEventStore>.Instance, detectionOptions, dashboardOptions);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private async Task SeedAsync(string signature, TimeSpan age, double botProbability, string? action)
    {
        await _store.AddDetectionAsync(new DashboardDetectionEvent
        {
            RequestId = Guid.NewGuid().ToString(),
            Timestamp = _time.GetUtcNow().UtcDateTime - age,
            IsBot = botProbability >= 0.5,
            BotProbability = botProbability,
            Confidence = 0.9,
            RiskBand = "Low",
            Method = "GET",
            Path = "/probe",
            StatusCode = 200,
            ProcessingTimeMs = 5.0,
            UserAgentRaw = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            Action = action,
            PrimarySignature = signature
        });
    }

    [Fact]
    public async Task Fold_tick_fuses_aged_low_importance_rows_when_enabled()
    {
        await SeedAsync("sig-old", TimeSpan.FromHours(6), 0.1, "allow");
        await SeedAsync("sig-young", TimeSpan.FromMinutes(30), 0.1, "allow");

        var options = Options.Create(new StyloBotDashboardOptions
        {
            DetectionRetention = TimeSpan.FromDays(30),
            TemporalStore = new TemporalStoreOptions { CompressionEnabled = true }
        });
        var sched = new FakeScheduleCoordinator();
        var fold = new DetectionCompressionFold(_store, options, sched, timeProvider: _time);
        await fold.StartAsync(default);

        await sched.RaiseTickAsync(TickCadence.Tick5m);

        // Old row fused away (drill-down excludes it); young row untouched.
        var rows = await _store.GetDetectionsAsync();
        rows.Should().ContainSingle(r => r.PrimarySignature == "sig-young");
        rows.Should().NotContain(r => r.PrimarySignature == "sig-old");
        rows.Single().Path.Should().Be("/probe");

        await fold.StopAsync(default);
    }

    [Fact]
    public async Task Fold_tick_is_a_no_op_when_compression_is_disabled()
    {
        await SeedAsync("sig-old", TimeSpan.FromHours(6), 0.1, "allow");

        var options = Options.Create(new StyloBotDashboardOptions
        {
            DetectionRetention = TimeSpan.FromDays(30),
            TemporalStore = new TemporalStoreOptions { CompressionEnabled = false }
        });
        var sched = new FakeScheduleCoordinator();
        var fold = new DetectionCompressionFold(_store, options, sched, timeProvider: _time);
        await fold.StartAsync(default);

        await sched.RaiseTickAsync(TickCadence.Tick5m);

        (await _store.GetDetectionsAsync()).Single().Path.Should().Be("/probe");

        await fold.StopAsync(default);
    }

    [Fact]
    public async Task Fold_self_disables_without_a_coordinator()
    {
        var options = Options.Create(new StyloBotDashboardOptions
        {
            TemporalStore = new TemporalStoreOptions { CompressionEnabled = true }
        });
        var fold = new DetectionCompressionFold(_store, options, schedule: null);
        await fold.StartAsync(default); // must not throw
        await fold.StopAsync(default);
    }

    [Fact]
    public async Task Pruner_tick_deletes_rows_past_configured_retention()
    {
        await SeedAsync("sig-expired", TimeSpan.FromDays(31), 0.1, "allow");
        await SeedAsync("sig-fresh", TimeSpan.FromHours(1), 0.1, "allow");

        var options = Options.Create(new StyloBotDashboardOptions
        {
            DetectionRetention = TimeSpan.FromDays(30)
        });
        var sched = new FakeScheduleCoordinator();
        var pruner = new DetectionRetentionPruner(_store, options, sched);
        await pruner.StartAsync(default);

        await sched.RaiseTickAsync(TickCadence.Tick1h);

        var rows = await _store.GetDetectionsAsync();
        rows.Should().ContainSingle(r => r.PrimarySignature == "sig-fresh");
        rows.Should().NotContain(r => r.PrimarySignature == "sig-expired");

        await pruner.StopAsync(default);
    }

    private sealed class FakeScheduleCoordinator : IScheduleCoordinator
    {
        private readonly List<(TickCadence Cadence, Func<DateTimeOffset, CancellationToken, Task> Handler)> _subs = new();

        public IDisposable Subscribe(TickCadence cadence, string subscriberName, CostHint costHint,
            Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            _subs.Add((cadence, handler));
            return new Sub(() => _subs.RemoveAll(s => s.Handler == handler));
        }

        public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();

        public async Task RaiseTickAsync(TickCadence cadence)
        {
            foreach (var s in _subs.Where(x => x.Cadence == cadence).ToList())
                await s.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
        }

        private sealed class Sub : IDisposable
        {
            private readonly Action _onDispose;
            public Sub(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }
}
