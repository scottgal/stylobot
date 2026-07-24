using FluentAssertions;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Telemetry;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Moq;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression coverage for the "dashboard_detections.domain is always 'unknown'"
///     production data-quality bug (1.49M rows, confirmed 2026-07-23).
///     <para>
///         Root cause: <see cref="DomainNormalizer.Resolve(HttpContext)"/> -- the only
///         code that ever stamps <see cref="HttpContextItemKeys.RequestScope"/> on
///         <c>HttpContext.Items</c> -- was never called anywhere on the live request
///         pipeline. Its only caller was the unregistered, unreachable
///         <c>EffectivePolicyResolver.ResolveThresholds</c> (dead code calling dead
///         code). <see cref="DetectionBroadcastMiddleware.BuildDetectionFromEvidence"/>
///         and <see cref="DetectionBroadcastMiddleware.BuildDetectionFromUpstream"/>
///         both read <c>HttpContextItemKeys.RequestScope</c> with a silent
///         <c>?? RequestScope.Unknown</c> fallback, so every row landed as "unknown".
///     </para>
///     <para>
///         Fix: <see cref="DetectionBroadcastMiddleware.InvokeAsync"/> now resolves
///         <see cref="DomainNormalizer"/> (when registered) and calls
///         <c>.Resolve(context)</c> unconditionally, before the pre-<c>_next</c> read
///         that feeds the upstream-trusted fast path -- so BOTH build branches see a
///         populated scope, regardless of whether local detection (AggregatedEvidence)
///         or upstream-header trust (BotDetectionResult) produced this event.
///     </para>
/// </summary>
public sealed class DetectionBroadcastMiddlewareDomainCaptureTests
{
    [Fact]
    public async Task LocalDetectionPath_captures_the_real_eTLD1_domain_and_full_host()
    {
        var eventStore = new CapturingEventStore();

        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new DetectionBroadcastMiddleware(next, NullLoggerFactory.CreateLogger());

        var ctx = NewHttpContext("shop.example.com");
        SeedLocalDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections.Should().HaveCount(1);
        eventStore.Detections[0].Domain.Should().Be("example.com",
            "the eTLD+1 of the request Host header must be captured, not the 'unknown' fallback");
        eventStore.Detections[0].Host.Should().Be("shop.example.com");
    }

    [Fact]
    public async Task InvokeAsync_stamps_RequestScope_unconditionally_before_either_build_branch_can_read_it()
    {
        // BuildDetectionFromUpstream is read PRE-_next (edge-header hydration fast path,
        // see InvokeAsync lines ~200-210): the Resolve() call must run before that first
        // read, not merely "somewhere before Build* runs" -- placing it after the pre-_next
        // try block would still leave that branch broken. This test proves the stamp lands
        // even when NEITHER AggregatedEvidence nor BotDetectionResult is present yet,
        // i.e. the resolve is unconditional and independent of which branch will later fire.
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new DetectionBroadcastMiddleware(next, NullLoggerFactory.CreateLogger());
        var ctx = NewHttpContext("api.acme.co.uk");
        var eventStore = new CapturingEventStore();

        await InvokeAsync(middleware, ctx, eventStore);

        var scope = (RequestScope)ctx.Items[HttpContextItemKeys.RequestScope]!;
        // acme.co.uk is a real two-label eTLD+1 under the co.uk public-suffix rule --
        // proves this is genuine PSL-aware normalization, not a naive last-two-labels split.
        scope.Domain.Should().Be("acme.co.uk");
        scope.Host.Should().Be("api.acme.co.uk");
    }

    [Fact]
    public void BuildDetectionFromUpstream_reads_the_Domain_and_Host_once_RequestScope_is_populated()
    {
        // Direct-call coverage for BuildDetectionFromUpstream's own Domain/Host read,
        // decoupled from the (separate, pre-existing) AggregatedEvidenceKey /
        // BotDetectionResultKey collision that currently makes InvokeAsync's upstream
        // branch unreachable -- see BotDetectionMiddleware.BotDetectionResultKey, which
        // aliases AggregatedEvidenceKey, self-contradicting the "!ContainsKey(A) &&
        // TryGetValue(A)" gate in InvokeAsync. That's a distinct bug outside this fix's
        // scope; this test locks in that BuildDetectionFromUpstream itself is correct
        // once RequestScope is on Items, however it got there.
        var middleware = NewMiddleware();
        var ctx = NewHttpContext("api.acme.co.uk");
        new DomainNormalizer(
            Options.Create(new DomainNormalizerOptions()),
            PublicSuffixList.LoadEmbedded()).Resolve(ctx);

        var detection = middleware.BuildDetectionFromUpstream(ctx, new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 0.9,
            BotName = "curl",
        });

        detection.Domain.Should().Be("acme.co.uk");
        detection.Host.Should().Be("api.acme.co.uk");
    }

    [Fact]
    public async Task Prefers_the_gateway_validated_SNI_over_a_spoofable_Host_header()
    {
        var eventStore = new CapturingEventStore();

        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new DetectionBroadcastMiddleware(next, NullLoggerFactory.CreateLogger());

        var ctx = NewHttpContext("evil-spoof.attacker.test");
        var feature = new FakeConnectionItems();
        feature.Items[TlsConnectionKeys.SniEvaluated] = true;
        feature.Items[TlsConnectionKeys.ValidatedSni] = "shop.example.com";
        ctx.Features.Set<IConnectionItemsFeature>(feature);
        SeedLocalDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections[0].Domain.Should().Be("example.com",
            "the TLS-validated SNI is authoritative over a client-supplied Host header");
        eventStore.Detections[0].Host.Should().Be("shop.example.com");
    }

    [Fact]
    public async Task DegradesToTheUnknownSentinel_when_DomainNormalizer_is_not_registered()
    {
        // A host that genuinely never wired AddBotDetection/AddStyloBotDashboard (so
        // DomainNormalizer isn't in the container) must still record SOMETHING rather
        // than throw. This is the one case "unknown" is the CORRECT answer.
        var eventStore = new CapturingEventStore();

        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new DetectionBroadcastMiddleware(next, NullLoggerFactory.CreateLogger());

        var ctx = NewHttpContext("shop.example.com", registerDomainNormalizer: false);
        SeedLocalDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections[0].Domain.Should().Be("unknown");
        eventStore.Detections[0].Host.Should().Be("unknown");
    }

    // --- helpers -----------------------------------------------------------

    private sealed class FakeConnectionItems : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    }

    private static DefaultHttpContext NewHttpContext(string host, bool registerDomainNormalizer = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/pricing";
        ctx.Request.Host = new HostString(host);
        ctx.Response.StatusCode = 200;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        ctx.TraceIdentifier = "test-request-" + Guid.NewGuid().ToString("N")[..8];

        var services = new ServiceCollection();
        if (registerDomainNormalizer)
        {
            services.AddSingleton(Options.Create(new DomainNormalizerOptions()));
            services.AddSingleton(PublicSuffixList.LoadEmbedded());
            services.AddSingleton<DomainNormalizer>();
        }
        ctx.RequestServices = services.BuildServiceProvider();
        return ctx;
    }

    private static void SeedLocalDetection(HttpContext ctx)
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.85,
            Confidence = 0.95,
            RiskBand = RiskBand.High,
            PrimaryBotName = "curl",
            PrimaryBotType = BotType.Tool,
            PolicyName = "default",
            TriggeredActionPolicyName = "throttle-tools",
            TotalProcessingTimeMs = 12.4,
        };
        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
    }

    private static DetectionBroadcastMiddleware NewMiddleware()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        return new DetectionBroadcastMiddleware(next, NullLoggerFactory.CreateLogger());
    }

    private static Task InvokeAsync(
        DetectionBroadcastMiddleware middleware,
        HttpContext ctx,
        IDashboardEventStore eventStore)
    {
        var hubCtxMock = new Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>(
            MockBehavior.Loose);
        var dashOptions = new StyloBotDashboardOptions();
        var detOptions = new BotDetectionOptions
        {
            ExcludeLocalIpFromBroadcast = false,
        };
        var sigCache = new SignatureAggregateCache(dashOptions);
        var publisher = new Mock<IDetectionEventPublisher>(MockBehavior.Loose);
        publisher.SetupGet(p => p.Name).Returns("test-publisher");
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<DetectionEvent>(), It.IsAny<CancellationToken>()))
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

    /// <summary>Hand-rolled <see cref="IDashboardEventStore"/> stub; see sibling
    /// <c>DetectionBroadcastPersistOnDownstreamThrowTests</c> for why (fire-and-forget
    /// Task.Run persist interacts poorly with mocks).</summary>
    private sealed class CapturingEventStore : IDashboardEventStore
    {
        public List<DashboardDetectionEvent> Detections { get; } = new();
        public List<DashboardSignatureEvent> Signatures { get; } = new();
        private readonly TaskCompletionSource _firstDetection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstSignature = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        {
            lock (Signatures)
            {
                Signatures.Add(signature);
                _firstSignature.TrySetResult();
            }
            return Task.FromResult(signature);
        }

        public Task WaitForDetectionsAsync(int expectedCount, int timeoutMs = 8000)
            => WaitForAsync(Detections, _firstDetection.Task, expectedCount, timeoutMs);

        private static async Task WaitForAsync<T>(List<T> bucket, Task signal, int expectedCount, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int seen;
                lock (bucket) seen = bucket.Count;
                if (seen >= expectedCount) return;
                if (DateTime.UtcNow >= deadline) return;
                await Task.WhenAny(signal, Task.Delay(50));
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
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
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

    private static class NullLoggerFactory
    {
        public static Microsoft.Extensions.Logging.ILogger<DetectionBroadcastMiddleware> CreateLogger()
            => Microsoft.Extensions.Logging.Abstractions.NullLogger<DetectionBroadcastMiddleware>.Instance;
    }
}
