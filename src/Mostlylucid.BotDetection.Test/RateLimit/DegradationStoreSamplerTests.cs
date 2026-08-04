using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the snapshot-on-Tick10s behaviour of
///     <see cref="DegradationStoreSampler"/>: each tick reads the
///     <see cref="DegradationAtom"/>'s current EWMA arms and persists one
///     <see cref="DegradationSnapshot"/> via
///     <see cref="IDashboardEventStore.RecordDegradationSnapshotAsync"/>.
///     <para>
///         Replaces the previous <c>DegradationHistorySamplerTests</c> +
///         <c>DegradationHistoryAtomTests</c> pair -- the atom is gone per
///         <c>feedback_no_inmemory_stores</c>.
///     </para>
/// </summary>
public class DegradationStoreSamplerTests
{
    [Fact]
    public async Task OnTickAsync_writes_one_snapshot_whose_arms_match_the_atom()
    {
        var atom = new DegradationAtom();
        // Drive the atom into a non-trivial state -- the gate's "outage
        // shape" is what the dashboard is meant to surface.
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(500, latencyMs: 25, path: "/");

        var store = new CapturingStore();
        var sampler = new DegradationStoreSampler(
            store, NullLogger<DegradationStoreSampler>.Instance, atom, null);

        await sampler.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(store.Recorded);
        var snap = store.Recorded[0];
        Assert.True(snap.Latency5xxRate > 0.9,
            $"5xx EWMA should reflect the atom; got {snap.Latency5xxRate}");
        Assert.True(snap.LatencyP95Ms > 0,
            $"latency EWMA should be populated; got {snap.LatencyP95Ms}");
    }

    [Fact]
    public async Task Multiple_ticks_persist_multiple_snapshots()
    {
        var atom = new DegradationAtom();
        var store = new CapturingStore();
        var sampler = new DegradationStoreSampler(
            store, NullLogger<DegradationStoreSampler>.Instance, atom, null);

        var t0 = DateTimeOffset.UtcNow;
        await sampler.OnTickAsync(t0, CancellationToken.None);
        await sampler.OnTickAsync(t0.AddSeconds(10), CancellationToken.None);
        await sampler.OnTickAsync(t0.AddSeconds(20), CancellationToken.None);

        Assert.Equal(3, store.Recorded.Count);
    }

    [Fact]
    public async Task Disposed_sampler_skips_subsequent_ticks()
    {
        var atom = new DegradationAtom();
        var store = new CapturingStore();
        var sampler = new DegradationStoreSampler(
            store, NullLogger<DegradationStoreSampler>.Instance, atom, null);

        await sampler.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        sampler.Dispose();
        await sampler.OnTickAsync(DateTimeOffset.UtcNow.AddSeconds(10), CancellationToken.None);

        Assert.Single(store.Recorded);
    }

    [Fact]
    public async Task GetDegradationHistoryAsync_round_trips_written_rows()
    {
        // Pins the read-side of the contract: any IDashboardEventStore
        // impl must return what RecordDegradationSnapshotAsync persisted,
        // ordered oldest-first. The capturing fake mirrors that contract
        // so the assertion is independent of any backing-store dialect.
        var atom = new DegradationAtom();
        var store = new CapturingStore();
        var sampler = new DegradationStoreSampler(
            store, NullLogger<DegradationStoreSampler>.Instance, atom, null);

        var t0 = DateTime.UtcNow.AddMinutes(-5);
        await sampler.OnTickAsync(new DateTimeOffset(t0, TimeSpan.Zero), CancellationToken.None);
        await sampler.OnTickAsync(new DateTimeOffset(t0.AddSeconds(10), TimeSpan.Zero), CancellationToken.None);

        var rows = await store.GetDegradationHistoryAsync(
            t0.AddMinutes(-1), DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].TimestampUtc <= rows[1].TimestampUtc);
    }

    /// <summary>
    ///     Minimal in-test fake. Stays scoped to this file so we don't
    ///     entangle the other dashboard-store stubs in this project
    ///     (each one's surface area is intentionally narrow per test
    ///     fixture).
    /// </summary>
    private sealed class CapturingStore : IDashboardEventStore
    {
        public List<DegradationSnapshot> Recorded { get; } = new();

        public Task RecordDegradationSnapshotAsync(
            DegradationSnapshot snapshot, CancellationToken ct = default)
        {
            lock (Recorded) Recorded.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
            DateTime startTime, DateTime endTime, CancellationToken ct = default)
        {
            lock (Recorded)
            {
                var slice = Recorded
                    .Where(r => r.TimestampUtc >= startTime && r.TimestampUtc <= endTime)
                    .OrderBy(r => r.TimestampUtc)
                    .ToList();
                return Task.FromResult<IReadOnlyList<DegradationSnapshot>>(slice);
            }
        }

        // -- unused surface --------------------------------------------------

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => Task.CompletedTask;
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => Task.FromResult(signature);
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => Task.FromResult(new List<DashboardDetectionEvent>());
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => Task.FromResult(new List<DashboardSignatureEvent>());
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) =>
            Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0,
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
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
