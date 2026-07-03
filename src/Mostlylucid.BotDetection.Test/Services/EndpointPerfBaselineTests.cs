using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class NullEndpointPerfBaselineTests
{
    [Fact]
    public void Null_baseline_returns_zero_for_any_input()
    {
        var baseline = new NullEndpointPerfBaseline();
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/"));
        Assert.Equal(0.0, baseline.GetExpectedMs("POST", "/api/users"));
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}"));
    }

    [Fact]
    public void Null_baseline_tolerates_empty_and_null_inputs()
    {
        var baseline = new NullEndpointPerfBaseline();
        Assert.Equal(0.0, baseline.GetExpectedMs("", ""));
        Assert.Equal(0.0, baseline.GetExpectedMs(null!, null!));
    }
}

public sealed class DashboardEventStoreBackedEndpointPerfBaselineTests
{
    /// <summary>
    ///     Fully-stubbed IDashboardEventStore that throws on every member
    ///     except GetEndpointStatsAsync. The baseline only consults that
    ///     one member; the rest of the FOSS interface (a wide surface) is
    ///     irrelevant to this test class.
    /// </summary>
    private class FakeStore : IDashboardEventStore
    {
        public virtual List<DashboardEndpointStats> Stats { get; set; } = new();

        public virtual Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(Stats);

        // --- Everything else throws. Bulk stubs to keep the file compilable. ---

        public Task AddDetectionAsync(DashboardDetectionEvent detection)
            => throw new System.NotSupportedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
            => throw new System.NotSupportedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0,
            bool? isBot = null) => throw new System.NotSupportedException();
        public Task<DashboardSummary> GetSummaryAsync(System.DateTime? startTime = null,
            System.DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new System.NotSupportedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(System.DateTime startTime,
            System.DateTime endTime, System.TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new System.NotSupportedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10,
            System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new System.NotSupportedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20,
            System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new System.NotSupportedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode,
            System.DateTime? startTime = null, System.DateTime? endTime = null)
            => throw new System.NotSupportedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature,
            int topN = 25, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path,
            System.DateTime? startTime = null, System.DateTime? endTime = null)
            => throw new System.NotSupportedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20,
            System.DateTime? startTime = null, System.DateTime? endTime = null)
            => throw new System.NotSupportedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
            => throw new System.NotSupportedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family,
            int hours = 168, CancellationToken ct = default)
            => throw new System.NotSupportedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50,
            System.DateTime? startTime = null, System.DateTime? endTime = null,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<int> PruneOldDetectionsAsync(System.DateTime cutoff, CancellationToken ct = default)
            => throw new System.NotSupportedException();
        public Task RecordDegradationSnapshotAsync(
            Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>
            GetDegradationHistoryAsync(System.DateTime startTime, System.DateTime endTime,
                CancellationToken ct = default) => throw new System.NotSupportedException();
    }

    private static DashboardEventStoreBackedEndpointPerfBaseline NewBaseline(
        FakeStore store, int minSamples = 30)
    {
        var opts = Options.Create(new BotDetectionOptions
        {
            PipelineLoadSensor = new PipelineLoadSensorOptions
            {
                MinSamplesForTrustedBaseline = minSamples,
            },
        });
        return new DashboardEventStoreBackedEndpointPerfBaseline(
            store, opts, scheduleCoordinator: null);
    }

    [Fact]
    public async Task GetExpectedMs_returns_zero_before_any_refresh()
    {
        var baseline = NewBaseline(new FakeStore());
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/"));
    }

    [Fact]
    public async Task Refresh_aggregates_raw_paths_into_normalized_templates()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 20 },
                new DashboardEndpointStats { Method = "GET", Path = "/users/456",
                    P95ProcessingTimeMs = 110, TotalCount = 20 },
            },
        };
        var baseline = NewBaseline(store, minSamples: 30);
        await baseline.RefreshNowAsync(CancellationToken.None);
        // Combined sample count (40) >= 30 -> baseline is trusted.
        // Weighted p95 across two rows with equal counts ~= 105ms.
        var actual = baseline.GetExpectedMs("GET", "/users/{id}");
        Assert.InRange(actual, 100, 110);
    }

    [Fact]
    public async Task Below_threshold_template_returns_zero()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 5 },
            },
        };
        var baseline = NewBaseline(store, minSamples: 30);
        await baseline.RefreshNowAsync(CancellationToken.None);
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/users/{id}"));
    }

    [Fact]
    public async Task Unknown_method_or_path_returns_zero()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 100 },
            },
        };
        var baseline = NewBaseline(store);
        await baseline.RefreshNowAsync(CancellationToken.None);
        Assert.Equal(0.0, baseline.GetExpectedMs("POST", "/users/{id}"));
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/admin"));
    }

    [Fact]
    public async Task Refresh_failure_preserves_prior_snapshot()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 100 },
            },
        };
        var baseline = NewBaseline(store);
        await baseline.RefreshNowAsync(CancellationToken.None);
        var before = baseline.GetExpectedMs("GET", "/users/{id}");
        Assert.True(before > 0);

        // Swap to a faulting store; the next refresh should fail silently
        // and leave the prior snapshot in place.
        var faulting = new FailingStore();
        var faulted = new DashboardEventStoreBackedEndpointPerfBaseline(
            faulting,
            Options.Create(new BotDetectionOptions
            {
                PipelineLoadSensor = new PipelineLoadSensorOptions { MinSamplesForTrustedBaseline = 30 },
            }),
            scheduleCoordinator: null);
        await faulted.RefreshNowAsync(CancellationToken.None);
        // The faulted baseline has no prior snapshot so its lookup is 0.
        Assert.Equal(0.0, faulted.GetExpectedMs("GET", "/users/{id}"));
        // The original baseline's snapshot is untouched.
        Assert.Equal(before, baseline.GetExpectedMs("GET", "/users/{id}"));
    }

    /// <summary>
    ///     Variant of FakeStore where GetEndpointStatsAsync throws. Used to
    ///     pin the "refresh failure preserves prior snapshot" contract.
    /// </summary>
    private sealed class FailingStore : FakeStore
    {
        public override Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new System.InvalidOperationException("store offline");
    }
}
