using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
///     Regression coverage for the 2026-06-16 dashboard_detections empty-table bug.
///     <para>
///         The detection pipeline ran on every request and the verdict was correct,
///         but every detection that triggered an action policy hit a downstream
///         "response has already started" exception inside YARP / a Minimal API
///         endpoint. <see cref="DetectionBroadcastMiddleware.InvokeAsync"/> had
///         <c>await _next(context)</c> placed OUTSIDE any try block on purpose
///         (commit <c>632e35fd</c>, to stop a double-invoke + mis-attributed
///         warning), but the write-behind persist block lived AFTER that line.
///         A downstream throw therefore short-circuited the persist call too,
///         so <see cref="IDashboardEventStore.AddDetectionAsync"/> was never
///         invoked and the dashboard_detections / dashboard_signatures tables
///         stayed empty even under heavy real traffic.
///     </para>
///     <para>
///         These tests drive the middleware end-to-end with a mocked event store
///         and a <c>_next</c> delegate that throws AFTER writing a response,
///         exactly mimicking the staging shape. The fix wraps the proxy call so
///         the persist + telemetry block in the <c>finally</c> still runs and
///         re-throws the downstream exception after the audit row is queued.
///     </para>
/// </summary>
public sealed class DetectionBroadcastPersistOnDownstreamThrowTests
{
    [Fact]
    public async Task AddDetectionAsync_is_called_even_when_next_throws_after_response_starts()
    {
        // Arrange ---------------------------------------------------------
        var eventStore = new CapturingEventStore();

        // Simulates the staging gateway shape: throttle-tools wrote a 429,
        // then a downstream Minimal API / YARP forwarder threw because the
        // response had already started.
        RequestDelegate next = _ =>
            throw new InvalidOperationException(
                "StatusCode cannot be set because the response has already started.");

        var logs = new List<string>();
        var middleware = new DetectionBroadcastMiddleware(next, new CapturingLogger(logs));

        var ctx = NewHttpContext();
        SeedDetection(ctx);

        // Act -------------------------------------------------------------
        // The middleware must propagate the downstream exception (so Kestrel
        // can map it to a 5xx) AND still queue the persist. We assert both
        // outcomes - exception bubbled up + AddDetectionAsync received the row.
        var act = async () => await InvokeAsync(middleware, ctx, eventStore);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The persist is fire-and-forget; wait up to 8 s so the Task.Run background
        // task completes even when the full test suite saturates the thread-pool.
        // BOTH writes -- detection and signature -- are awaited inside the same
        // Task.Run, so the signature lands AFTER the detection TCS fires. Wait
        // for the signature TCS too or the count assertion races the second await.
        await eventStore.WaitForDetectionsAsync(1, timeoutMs: 8000);
        await eventStore.WaitForSignaturesAsync(1, timeoutMs: 8000);

        // Assert ----------------------------------------------------------
        eventStore.Detections.Should().HaveCount(1,
            "the detection MUST land in dashboard_detections even when the " +
            "downstream proxy / endpoint throws after writing the response. " +
            "Logs: " + string.Join(" | ", logs));
        eventStore.Detections[0].Path.Should().Be("/pricing");
        eventStore.Detections[0].PrimarySignature.Should().NotBeNullOrEmpty();
        eventStore.Detections[0].BotProbability.Should().BeApproximately(0.85, precision: 0.001);

        eventStore.Signatures.Should().HaveCount(1,
            "the signature row must also be written so the dashboard signature " +
            "list survives downstream failures");
    }

    [Fact]
    public async Task Records_the_downstream_final_status_not_the_pre_next_default()
    {
        // Honeypot shape: detection evidence is present BEFORE _next (the gateway/upstream
        // path pre-populates context.Items), and the DOWNSTREAM returns 404 (Deflect404
        // honeypot mode) or 400. The recorded status_code MUST be the downstream FINAL
        // status so the dashboard's status filter (400/4xx) and honeypot view show the hit --
        // not whatever the status was before _next ran. This is the "record status properly"
        // contract, and the exact class that made the 400 filter + honeypot view go empty.
        var eventStore = new CapturingEventStore();

        RequestDelegate next = c =>
        {
            c.Response.StatusCode = 404; // honeypot deflect / not-found
            return Task.CompletedTask;
        };

        var logs = new List<string>();
        var middleware = new DetectionBroadcastMiddleware(next, new CapturingLogger(logs));

        var ctx = NewHttpContext();
        ctx.Response.StatusCode = 200; // pre-_next default, before the downstream sets 404
        SeedDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections.Should().HaveCount(1, "Logs: " + string.Join(" | ", logs));
        eventStore.Detections[0].StatusCode.Should().Be(404,
            "the recorded status must be the downstream FINAL status (honeypot 404), not the " +
            "pre-_next default (200) -- otherwise the dashboard 400/status filter + honeypot view go empty");
    }

    [Fact]
    public async Task AddDetectionAsync_is_called_on_the_happy_path_no_downstream_throw()
    {
        // Baseline sanity: the happy path must keep persisting. Guards against
        // a regression where the try/finally accidentally swallows the persist
        // when _next succeeds.
        var eventStore = new CapturingEventStore();

        RequestDelegate next = _ => Task.CompletedTask;

        var logs = new List<string>();
        var middleware = new DetectionBroadcastMiddleware(next, new CapturingLogger(logs));

        var ctx = NewHttpContext();
        SeedDetection(ctx);

        await InvokeAsync(middleware, ctx, eventStore);

        await eventStore.WaitForDetectionsAsync(1);
        eventStore.Detections.Should().HaveCount(1, "Logs: " + string.Join(" | ", logs));
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    ///     Hand-rolled <see cref="IDashboardEventStore"/> stub. Mock-based variants
    ///     interacted poorly with the fire-and-forget <c>Task.Run</c> path the
    ///     middleware uses for write-behind persist (the proxy's awaiter races
    ///     the test deadline; concrete impl keeps the assertions deterministic).
    /// </summary>
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

        public Task WaitForDetectionsAsync(int expectedCount, int timeoutMs = 2000)
            => WaitForAsync(Detections, _firstDetection.Task, expectedCount, timeoutMs);

        public Task WaitForSignaturesAsync(int expectedCount, int timeoutMs = 2000)
            => WaitForAsync(Signatures, _firstSignature.Task, expectedCount, timeoutMs);

        private static async Task WaitForAsync<T>(List<T> bucket, Task signal, int expectedCount, int timeoutMs)
        {
            // Count check comes BEFORE the deadline check on every iteration,
            // including the final one. The previous code checked the deadline first
            // which meant a detection arriving right at the boundary was missed:
            // we'd exit the while condition as false and never see Count==1.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int seen;
                lock (bucket) seen = bucket.Count;
                if (seen >= expectedCount) return;
                if (DateTime.UtcNow >= deadline) return;
                // Wait for the TCS signal (fired by AddXxxAsync) or a 50ms
                // poll tick -- whichever arrives first. 50ms > 20ms to reduce
                // thread-pool pressure under the full parallel test suite.
                await Task.WhenAny(signal, Task.Delay(50));
            }
        }

        // Surfaces not exercised by the broadcast middleware persist path -
        // throw so a future refactor that lands here is caught early.
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

    [Fact]
    public void InvokeAsync_does_not_declare_IDashboardChangeCursor_parameter()
    {
        // Regression (#47 / Plan 3 gateway 500): ASP.NET convention-based middleware
        // resolves EVERY InvokeAsync parameter via GetRequiredService -- nullability and
        // default values are ignored -- so an "optional" service parameter throws on any
        // host that does not register it (the lean gateway dashboard path did not register
        // IDashboardChangeCursor -> 500 on every request). The cursor must be resolved from
        // RequestServices via GetService INSIDE InvokeAsync, never declared as a parameter.
        typeof(DetectionBroadcastMiddleware).GetMethod(nameof(DetectionBroadcastMiddleware.InvokeAsync))!
            .GetParameters()
            .Should().NotContain(
                p => p.ParameterType == typeof(IDashboardChangeCursor),
                "convention-based middleware cannot optionally resolve a service param; " +
                "resolve IDashboardChangeCursor via RequestServices.GetService inside InvokeAsync");
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

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/pricing";
        ctx.Response.StatusCode = 429;
        // Public-shape IP so the local-IP guard doesn't suppress persist.
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();
        ctx.TraceIdentifier = "test-request-" + Guid.NewGuid().ToString("N")[..8];
        return ctx;
    }

    private static void SeedDetection(HttpContext ctx)
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

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<DetectionBroadcastMiddleware>
    {
        private readonly List<string> _logs;
        public CapturingLogger(List<string> logs) { _logs = logs; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_logs) _logs.Add($"[{logLevel}] {formatter(state, exception)}" +
                (exception is not null ? " ex=" + exception.GetType().Name + ":" + exception.Message : ""));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}