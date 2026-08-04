using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Telemetry;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins <see cref="DetectionBroadcastMiddleware.InvokeAsync"/>'s post-_next capture of
///     the gateway-stamped real origin status (mae-'s Endpoints UPSTREAM/RETURNED spec).
///     Mirrors the harness in <c>DetectionBroadcastPersistOnDownstreamThrowTests</c>.
/// </summary>
public sealed class DetectionBroadcastUpstreamStatusCaptureTests
{
    [Fact]
    public async Task UpstreamStatusCode_is_captured_when_the_gateway_transform_stamped_it()
    {
        // Forwarded case: YARP's UpstreamStatusTransform ran as part of _next (inside
        // MapReverseProxy) and stamped the real origin status before returning.
        var eventStore = new CapturingEventStore();
        RequestDelegate next = c =>
        {
            c.Items[BotDetectionMiddleware.UpstreamStatusCodeItemKey] = 200;
            c.Response.StatusCode = 200;
            return Task.CompletedTask;
        };
        var middleware = new DetectionBroadcastMiddleware(next, Microsoft.Extensions.Logging.Abstractions.NullLogger<DetectionBroadcastMiddleware>.Instance);
        var ctx = NewHttpContext();
        SeedDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections.Should().HaveCount(1);
        eventStore.Detections[0].UpstreamStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task UpstreamStatusCode_is_null_when_the_request_never_reached_the_origin()
    {
        // Honeypot/blocked/throttled shape: the response is synthesised by StyloBot's own
        // enforcement gates before MapReverseProxy ever runs, so the gateway transform never
        // fires and never stamps the key. Null is the correct "no real origin call" signal.
        var eventStore = new CapturingEventStore();
        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 404; // honeypot deflect, no origin call
            return Task.CompletedTask;
        };
        var middleware = new DetectionBroadcastMiddleware(next, Microsoft.Extensions.Logging.Abstractions.NullLogger<DetectionBroadcastMiddleware>.Instance);
        var ctx = NewHttpContext();
        SeedDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections.Should().HaveCount(1);
        eventStore.Detections[0].UpstreamStatusCode.Should().BeNull();
    }

    // --- helpers (mirrors DetectionBroadcastPersistOnDownstreamThrowTests) ---

    private sealed class CapturingEventStore : IDashboardEventStore
    {
        public List<DashboardDetectionEvent> Detections { get; } = new();
        private readonly TaskCompletionSource _firstDetection = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AddDetectionAsync(DashboardDetectionEvent detection)
        {
            lock (Detections)
            {
                Detections.Add(detection);
                _firstDetection.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
            => Task.FromResult(signature);

        public async Task WaitForDetectionsAsync(int expectedCount, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int seen;
                lock (Detections) seen = Detections.Count;
                if (seen >= expectedCount) return;
                if (DateTime.UtcNow >= deadline) return;
                await Task.WhenAny(_firstDetection.Task, Task.Delay(50));
            }
        }

        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
            => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
            => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
            => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null)
            => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
            => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(
            Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>
            GetDegradationHistoryAsync(DateTime startTime, DateTime endTime,
                CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static Task InvokeAsync(
        DetectionBroadcastMiddleware middleware,
        HttpContext ctx,
        IDashboardEventStore eventStore)
    {
        var hubCtxMock = new Moq.Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>(Moq.MockBehavior.Loose);
        var dashOptions = new StyloBotDashboardOptions();
        var detOptions = new BotDetectionOptions { ExcludeLocalIpFromBroadcast = false };
        var sigCache = new SignatureAggregateCache(dashOptions);
        var publisher = new Moq.Mock<IDetectionEventPublisher>(Moq.MockBehavior.Loose);
        publisher.SetupGet(p => p.Name).Returns("test-publisher");
        publisher
            .Setup(p => p.PublishAsync(Moq.It.IsAny<DetectionEvent>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        return middleware.InvokeAsync(
            ctx,
            hubCtxMock.Object,
            eventStore,
            Options.Create(detOptions),
            Options.Create(dashOptions),
            sigCache,
            publisher.Object);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/pricing";
        ctx.Response.StatusCode = 200;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();
        ctx.TraceIdentifier = "test-request-" + Guid.NewGuid().ToString("N")[..8];
        return ctx;
    }

    private static void SeedDetection(HttpContext ctx)
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.1,
            Confidence = 0.5,
            RiskBand = RiskBand.Low,
            PolicyName = "default",
            TotalProcessingTimeMs = 3.1,
        };
        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
    }
}
