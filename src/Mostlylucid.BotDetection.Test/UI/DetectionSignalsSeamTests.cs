using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
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
///     The end-to-end seam for per-detection signals: real evidence -> real
///     <see cref="DetectionBroadcastMiddleware"/> build -> real
///     <see cref="SqliteDashboardEventStore"/> -> read back with the signals intact.
///     <para>
///         Answers the "why do most detection rows carry no signals" question with a
///         measurement rather than an inference: the built dict is counted per detection
///         (<see cref="DetectionBroadcastMiddleware.SignalsBuiltTotal"/> /
///         <see cref="DetectionBroadcastMiddleware.DetectionsBuiltTotal"/>), and the last
///         test drives the REAL detector pipeline to prove <c>evidence.Signals</c> is not
///         itself sparse — the drop was at the persistence boundary, not the evidence seam.
///     </para>
/// </summary>
public sealed class DetectionSignalsSeamTests : IDisposable
{
    private readonly string _tempDir;

    public DetectionSignalsSeamTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-signals-seam-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }

    [Fact]
    public void BuildDetectionFromEvidence_carries_the_evidence_signals_into_ImportantSignals()
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.83,
            Confidence = 0.9,
            RiskBand = RiskBand.High,
            PrimaryBotName = "Bytespider",
            TotalProcessingTimeMs = 9.1,
            Signals = new Dictionary<string, object>
            {
                ["ua.family"] = "Bytespider",
                ["ua.bot_name"] = "Bytespider",
                ["tls.version"] = "TLSv1.3",
                ["headless.indicator"] = true,
                ["request.path"] = "/pricing",
            },
        };

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);

        detection.ImportantSignals.Should().NotBeNull();
        detection.ImportantSignals!.Should().ContainKeys(
            "ua.family", "ua.bot_name", "tls.version", "headless.indicator", "request.path",
            "request.protocol");
        detection.ImportantSignals!["headless.indicator"].Should().Be(true);
    }

    [Fact]
    public async Task Middleware_persist_then_store_read_returns_the_signals()
    {
        // The full seam: the middleware's write-behind persist into the REAL SQLite
        // store, then the exact read GetDetectionsAsync performs for the signature
        // detail page. Before the fix the read returned null for every row.
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            ExcludeLocalIpFromBroadcast = false,
        });
        var store = new SqliteDashboardEventStore(
            NullLogger<SqliteDashboardEventStore>.Instance, options);

        var ctx = NewHttpContext();
        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = new AggregatedEvidence
        {
            BotProbability = 0.91,
            Confidence = 0.95,
            RiskBand = RiskBand.VeryHigh,
            PrimaryBotName = "GPTBot",
            TotalProcessingTimeMs = 11.2,
            Signals = new Dictionary<string, object>
            {
                ["ua.family"] = "GPTBot",
                ["ai.scraper"] = true,
                ["request.protocol"] = "HTTP/2",
            },
        };

        await InvokeAsync(NewMiddleware(), ctx, store);

        var rows = await WaitForDetectionsAsync(store, 1);
        rows.Should().ContainSingle();

        var signals = rows[0].ImportantSignals;
        signals.Should().NotBeNull(
            "the persisted row must carry the signals the broadcast path built");
        signals!.Should().ContainKeys("ua.family", "ai.scraper");
        signals["ua.family"].Should().Be("GPTBot");
        signals["ai.scraper"].Should().Be(true);
    }

    [Fact]
    public void Signals_built_instrument_reports_a_non_zero_mean_per_detection()
    {
        var middleware = NewMiddleware();
        var ctx = NewHttpContext();
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.2,
            Confidence = 0.5,
            RiskBand = RiskBand.Low,
            TotalProcessingTimeMs = 1,
            Signals = new Dictionary<string, object>
            {
                ["ua.family"] = "Chrome",
                ["request.protocol"] = "HTTP/2",
            },
        };

        var before = DetectionBroadcastMiddleware.DetectionsBuiltTotal;
        middleware.BuildDetectionFromEvidence(ctx, evidence);
        var after = DetectionBroadcastMiddleware.DetectionsBuiltTotal;

        after.Should().BeGreaterThan(before, "the instrument must count every build");
        DetectionBroadcastMiddleware.SignalsBuiltTotal.Should().BeGreaterThanOrEqualTo(after,
            "the running total can never be smaller than the detection count (>=1 key per build)");
    }

    /// <summary>
    ///     Drives the REAL wired detector pipeline (AddBotDetection + the real
    ///     <see cref="BotDetectionOrchestrator"/>) and measures <c>evidence.Signals</c>.
    ///     This is the measurement that rules out "the evidence seam is sparse" as the
    ///     cause of empty detection rows: a single ordinary request produces a populated
    ///     signal dict, so an empty persisted row can only come from the persistence
    ///     boundary dropping it.
    /// </summary>
    [Fact]
    public async Task Real_pipeline_evidence_Signals_is_populated_for_an_ordinary_request()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = Path.Combine(_tempDir, "pipeline.db"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpContextAccessor();
        services.AddBotDetection();

        await using var provider = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = provider };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/pricing";
        ctx.Request.Host = new HostString("shop.example.com");
        ctx.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        ctx.Request.Headers.Accept = "text/html,application/xhtml+xml";
        ctx.Request.Headers.AcceptLanguage = "en-GB,en;q=0.9";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        ctx.TraceIdentifier = "seam-" + Guid.NewGuid().ToString("N")[..8];

        var orchestrator = provider.GetRequiredService<BotDetectionOrchestrator>();
        var evidence = await orchestrator.DetectAsync(ctx);

        evidence.Signals.Should().NotBeNull();
        // The measurement: an ordinary request produces a richly populated dict. The
        // exact count is an implementation detail; the floor is what falsifies "the
        // evidence seam is sparse".
        evidence.Signals.Count.Should().BeGreaterThan(15,
            "the real pipeline raises signals onto the sink and projects them into evidence.Signals");
        evidence.Signals.Should().ContainKeys("request.method", "request.path", "hydration.complete");
    }

    /// <summary>
    ///     The regression pin for the "detections carry no important signals" symptom.
    ///     <para>
    ///         MEASURED before the fix (real wired pipeline, one ordinary Chrome request):
    ///         <c>evidence.Signals</c> carried 157 keys, 134 of them the taxonomy's
    ///         <c>detector.{Name}.started|completed</c> lifecycle pairs. The dashboard
    ///         build caps at 80 keys by enumeration order, so the cap retained only
    ///         hydration noise + bookkeeping and every detection finding
    ///         (<c>score.bot_probability</c>, <c>is_human</c>, <c>risk.justification</c>,
    ///         <c>cve.match_count</c>) sat past the cut and was discarded. After the fix
    ///         the same request projects 27 keys and the findings are IN the built dict.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Built_important_signals_carry_detection_findings_not_detector_lifecycle_noise()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = Path.Combine(_tempDir, "findings.db"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpContextAccessor();
        services.AddBotDetection();
        await using var provider = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = provider };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/pricing";
        ctx.Request.Host = new HostString("shop.example.com");
        ctx.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        ctx.TraceIdentifier = "findings-" + Guid.NewGuid().ToString("N")[..8];

        var orchestrator = provider.GetRequiredService<BotDetectionOrchestrator>();
        var evidence = await orchestrator.DetectAsync(ctx);

        // The projection must not treat per-atom lifecycle bookkeeping as a signal.
        evidence.Signals.Keys.Should().NotContain(k => k.StartsWith("detector.", StringComparison.Ordinal),
            "detector.{Name}.started/completed is orchestrator bookkeeping and must not occupy the signal cap");

        var detection = NewMiddleware().BuildDetectionFromEvidence(ctx, evidence);

        detection.ImportantSignals.Should().NotBeNull();
        detection.ImportantSignals!.Should().ContainKeys("score.bot_probability", "is_human");
        detection.ImportantSignals!.Keys.Should().NotContain(k => k.StartsWith("detector.", StringComparison.Ordinal));
        detection.ImportantSignals!.Count.Should().BeLessThan(
            DashboardSignals.MaxSignalsPerDetection,
            "with the bookkeeping skipped a real request no longer hits the truncation cap");
    }

    // --- helpers -----------------------------------------------------------

    private static DetectionBroadcastMiddleware NewMiddleware()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        return new DetectionBroadcastMiddleware(next, NullLogger<DetectionBroadcastMiddleware>.Instance);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/pricing";
        ctx.Request.Host = new HostString("shop.example.com");
        ctx.Response.StatusCode = 200;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        ctx.TraceIdentifier = "seam-ctx-" + Guid.NewGuid().ToString("N")[..8];
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();
        return ctx;
    }

    private static async Task<List<DashboardDetectionEvent>> WaitForDetectionsAsync(
        IDashboardEventStore store, int expected)
    {
        // The persist is fire-and-forget (write-behind); poll briefly for the row.
        for (var i = 0; i < 100; i++)
        {
            var rows = await store.GetDetectionsAsync();
            if (rows.Count >= expected) return rows;
            await Task.Delay(25);
        }
        return await store.GetDetectionsAsync();
    }

    private static Task InvokeAsync(
        DetectionBroadcastMiddleware middleware,
        HttpContext ctx,
        IDashboardEventStore eventStore)
    {
        var hubCtxMock = new Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>(MockBehavior.Loose);
        var dashOptions = new StyloBotDashboardOptions();
        var detOptions = new BotDetectionOptions { ExcludeLocalIpFromBroadcast = false };
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
}
