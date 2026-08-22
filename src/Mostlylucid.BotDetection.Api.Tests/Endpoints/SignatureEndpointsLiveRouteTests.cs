using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Tests.Endpoints;

/// <summary>
///     Live-route proof for <c>GET /api/v1/signatures/{id}</c> and
///     <c>GET /api/v1/signatures/{id}/timeseries</c> (dash-/foss- 2026-08-22 DIM-gap fix).
///     <para>
///     This is the exact class of gap that produced 4 silent-empty deploys: client code
///     (<c>RemoteDashboardEventStore</c>) existed, store code (<c>TryGetSignatureAsync</c>,
///     <c>GetSignatureTimeSeriesAsync</c>) existed, but the HTTP route registered in between
///     was never wired -- a 404 that nothing threw on, degrading silently to empty. A unit test
///     calling the handler method directly cannot catch this class of bug (the handler works
///     fine in isolation); only a real HTTP call through the actual route table proves the
///     route exists and dispatches to the real store method.
///     </para>
///     Boots a real minimal-API <see cref="WebApplication"/> with <see cref="TestServer"/>,
///     registers <see cref="MapReadEndpoints"/> exactly as the gateway does, and issues real
///     HTTP requests through <c>app.GetTestClient()</c> -- no shortcuts through the handler
///     delegate.
/// </summary>
public sealed class SignatureEndpointsLiveRouteTests
{
    private const string ApiKeyId = "test-key";
    private const string ApiKeyHeader = "X-SB-Api-Key";

    private static async Task<(WebApplication App, HttpClient Client, SpyStore Store)> BuildHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.Configure<BotDetectionOptions>(o =>
        {
            o.ApiKeys[ApiKeyId] = new ApiKeyConfig { Key = ApiKeyId };
        });

        var store = new SpyStore();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddStyloBotApi();

        var app = builder.Build();
        app.MapReadEndpoints();

        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ApiKeyHeader, ApiKeyId);
        return (app, client, store);
    }

    [Fact]
    public async Task GetSignatureDetail_DispatchesToTryGetSignatureAsync_AndReturns200()
    {
        var (app, client, store) = await BuildHostAsync();
        await using var _ = app;

        var response = await client.GetAsync("/api/v1/signatures/sig-abc123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, store.TryGetSignatureCallCount);
        Assert.Equal("sig-abc123", store.LastRequestedSignatureId);
    }

    [Fact]
    public async Task GetSignatureDetail_UnknownSignature_Returns404_NotSilentEmpty()
    {
        var (app, client, store) = await BuildHostAsync();
        await using var _ = app;
        store.NextSignatureResult = null;

        var response = await client.GetAsync("/api/v1/signatures/never-seen");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, store.TryGetSignatureCallCount);
    }

    [Fact]
    public async Task GetSignatureTimeseries_DispatchesToGetSignatureTimeSeriesAsync_AndReturns200()
    {
        var (app, client, store) = await BuildHostAsync();
        await using var _ = app;

        var response = await client.GetAsync("/api/v1/signatures/sig-abc123/timeseries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, store.GetSignatureTimeSeriesCallCount);
        Assert.Equal("sig-abc123", store.LastTimeseriesSignatureId);
    }

    /// <summary>
    ///     Spy <see cref="IDashboardEventStore"/>: only overrides the 2 methods under test,
    ///     all other members fall through to the interface's default implementations
    ///     (empty results) since this store is never exercised for them by these routes.
    /// </summary>
    private sealed class SpyStore : IDashboardEventStore
    {
        public int TryGetSignatureCallCount;
        public int GetSignatureTimeSeriesCallCount;
        public string? LastRequestedSignatureId;
        public string? LastTimeseriesSignatureId;
        public DashboardSignatureEvent? NextSignatureResult = new()
        {
            SignatureId = "sig-abc123",
            Timestamp = DateTime.UtcNow,
            PrimarySignature = "sig-abc123",
            RiskBand = "Low"
        };

        public Task<DashboardSignatureEvent?> TryGetSignatureAsync(string signatureId, CancellationToken ct = default)
        {
            TryGetSignatureCallCount++;
            LastRequestedSignatureId = signatureId;
            return Task.FromResult(NextSignatureResult);
        }

        public Task<IReadOnlyList<DashboardSignatureTimeSeriesPoint>> GetSignatureTimeSeriesAsync(
            string signature, DateTime startTime, DateTime endTime, TimeSpan bucketSize, CancellationToken ct = default)
        {
            GetSignatureTimeSeriesCallCount++;
            LastTimeseriesSignatureId = signature;
            return Task.FromResult<IReadOnlyList<DashboardSignatureTimeSeriesPoint>>([
                new DashboardSignatureTimeSeriesPoint(startTime, 1, 0.5, 0.5, 10)
            ]);
        }

        // Remaining members -- not exercised by these routes.
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
