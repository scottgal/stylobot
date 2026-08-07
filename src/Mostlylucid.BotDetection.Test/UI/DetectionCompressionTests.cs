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
}

/// <summary>
///     The compression fold against a real SQLite store: aged low-importance
///     rows lose their per-request detail columns; young rows and
///     important-enough rows keep detail until full absorption; dashboard reads
///     return identical shapes either way. This is the single temporal store —
///     no bucket tables, the row is its own summary.
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
        double? threatScore = null)
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
            CountryCode = "GB",
            RiskJustification = "seed justification",
            ReferrerHost = "referrer.example",
            UaDeviceClass = "desktop"
        };

    [Fact]
    public async Task Schema_has_importance_weight_column_and_insert_persists_it()
    {
        await _store.GetDetectionsAsync(); // triggers init

        var low = Event("sig-low", DateTime.UtcNow.AddHours(-3), 0.1, "allow", null);
        var high = Event("sig-high", DateTime.UtcNow.AddHours(-3), 0.9, "block", 1.0);
        await _store.AddDetectionAsync(low);
        await _store.AddDetectionAsync(high);

        await using var conn = new SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "dashboard.db")}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT signature, importance_weight FROM detections
            ORDER BY importance_weight ASC
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Sig, double Weight)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetDouble(1)));

        rows.Should().ContainSingle(r => r.Sig == "sig-low" && r.Weight < 0.4);
        rows.Should().ContainSingle(r => r.Sig == "sig-high" && r.Weight >= 0.4);
    }

    [Fact]
    public async Task Fold_nulls_detail_but_keeps_aggregate_columns()
    {
        var now = DateTime.UtcNow;
        var low = Event("sig-fold", now.AddHours(-6), 0.1, "allow", null);
        await _store.AddDetectionAsync(low);

        var folded = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);
        folded.Should().Be(1);

        var detections = await _store.GetDetectionsAsync();
        var row = detections.Should().ContainSingle().Subject;

        // Per-request detail folded — the read path's own missing-value
        // convention applies (NULL method reads as "", NULL path as "/");
        // the nullable detail columns read as null.
        row.Method.Should().BeEmpty();
        row.Path.Should().Be("/");
        row.UserAgentRaw.Should().BeNull();
        row.RiskJustification.Should().BeNull();
        row.ReferrerHost.Should().BeNull();
        row.UaDeviceClass.Should().BeNull();

        // …while everything the dashboard aggregates on survives intact.
        row.BotProbability.Should().BeApproximately(0.1, 0.001);
        row.RiskBand.Should().Be("Low");
        row.IsBot.Should().BeFalse();
        row.CountryCode.Should().Be("GB");
        row.Action.Should().Be("allow");
        row.ResponseBytes.Should().Be(4096);
        row.ProcessingTimeMs.Should().Be(5.0);
        row.PrimarySignature.Should().Be("sig-fold");
    }

    [Fact]
    public async Task Young_rows_are_never_folded()
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
    public async Task Fold_is_batched_and_drains_lowest_importance_first()
    {
        var now = DateTime.UtcNow;
        // Both 6h old (past hot cutoff, before full absorption). weights:
        // low ≈ 0.05, mid ≈ 0.08 (rate-limit), both < floor 0.4.
        await _store.AddDetectionAsync(Event("sig-lowest", now.AddHours(-6), 0.1, "allow", null));
        await _store.AddDetectionAsync(Event("sig-mid", now.AddHours(-6), 0.0, "rate-limit", null));

        var first = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, batchSize: 1);
        first.Should().Be(1);
        var afterFirst = await _store.GetDetectionsAsync();
        afterFirst.Single(r => r.PrimarySignature == "sig-lowest").Path.Should().Be("/");
        afterFirst.Single(r => r.PrimarySignature == "sig-mid").Path.Should().Be("/probe");

        var second = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, batchSize: 1);
        second.Should().Be(1);
        (await _store.GetDetectionsAsync()).Single(r => r.PrimarySignature == "sig-mid").Path.Should().Be("/");

        // Idempotent: nothing left to fold.
        var third = await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, batchSize: 1);
        third.Should().Be(0);
    }

    [Fact]
    public async Task Summary_and_timeseries_are_identical_after_folding()
    {
        var now = DateTime.UtcNow;
        await _store.AddDetectionAsync(Event("sig-a", now.AddHours(-6), 0.1, "allow", null));
        await _store.AddDetectionAsync(Event("sig-b", now.AddHours(-6), 0.9, "block", 1.0));
        await _store.AddDetectionAsync(Event("sig-c", now.AddHours(-1), 0.2, "allow", null));

        var beforeSummary = await _store.GetSummaryAsync(now.AddHours(-24), now);
        var beforeSeries = await _store.GetTimeSeriesAsync(
            now.AddHours(-24), now, TimeSpan.FromHours(1));

        await _store.FoldAgedDetectionsAsync(
            now.AddHours(-2), now.AddHours(-48), Defaults.ImportanceFloor, Defaults.FoldBatchSize);

        var afterSummary = await _store.GetSummaryAsync(now.AddHours(-24), now);
        var afterSeries = await _store.GetTimeSeriesAsync(
            now.AddHours(-24), now, TimeSpan.FromHours(1));

        // Counts AND the numeric KPI columns (bytes/ms) survive — the fold only
        // nulls the verbose per-request TEXT detail.
        afterSummary.TotalRequests.Should().Be(beforeSummary.TotalRequests);
        afterSummary.BotRequests.Should().Be(beforeSummary.BotRequests);
        afterSummary.HumanRequests.Should().Be(beforeSummary.HumanRequests);
        afterSummary.BytesOut.Should().Be(beforeSummary.BytesOut);
        afterSummary.AverageProcessingTimeMs.Should().Be(beforeSummary.AverageProcessingTimeMs);

        afterSeries.Should().HaveCount(beforeSeries.Count);
        var afterTotal = afterSeries.Sum(p => p.TotalCount);
        var beforeTotal = beforeSeries.Sum(p => p.TotalCount);
        afterTotal.Should().Be(beforeTotal);
        var afterBots = afterSeries.Sum(p => p.BotCount);
        var beforeBots = beforeSeries.Sum(p => p.BotCount);
        afterBots.Should().Be(beforeBots);
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
    public async Task Fold_tick_folds_aged_low_importance_rows_when_enabled()
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

        var rows = await _store.GetDetectionsAsync();
        rows.Single(r => r.PrimarySignature == "sig-old").Path.Should().Be("/");
        rows.Single(r => r.PrimarySignature == "sig-young").Path.Should().Be("/probe");

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
