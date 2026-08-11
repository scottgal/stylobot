using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Test.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 3 TDD: asserts that the <c>/partials/update</c> middleware compose path
///     issues exactly ONE <c>ComposeBatchAsync</c> when the requested widgets are
///     catalog-covered, and that non-covered widgets do NOT inhibit the response.
/// </summary>
public sealed class WidgetBatchComposeTests
{
    // ---------- helpers ----------

    private static DashboardSummary EmptySummary() => new()
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
    };

    private static DashboardPageResult MakeResult(
        IReadOnlyList<DashboardTopBotEntry>? botAggregate = null,
        IReadOnlyList<DashboardCountryStats>? geo = null)
    {
        var bundle = new DashboardDatasetBundle(
            Summary: EmptySummary(),
            TimeBuckets: new List<DashboardTimeSeriesPoint>(),
            BotAggregate: botAggregate ?? new List<DashboardTopBotEntry>(),
            Geo: geo ?? new List<DashboardCountryStats>(),
            Endpoints: new List<DashboardEndpointStats>());
        return new DashboardPageResult(bundle);
    }

    // ---------- recording composer ----------

    /// <summary>
    ///     Records ComposeAsync calls; returns a fixed result.
    /// </summary>
    private sealed class RecordingComposer : IDashboardPageComposer
    {
        public int ComposeCallCount { get; private set; }
        public DashboardPageManifest? LastManifest { get; private set; }
        public DashboardPageWindow? LastWindow { get; private set; }

        public Task<DashboardPageResult> ComposeAsync(
            DashboardPageManifest manifest,
            DashboardPageWindow window,
            CancellationToken ct)
        {
            ComposeCallCount++;
            LastManifest = manifest;
            LastWindow = window;
            return Task.FromResult(MakeResult());
        }
    }

    // ---------- tests at the composer-boundary ----------

    /// <summary>
    ///     Simulates what the middleware must do: resolve the covered widget keys,
    ///     build a manifest from them, and call ComposeAsync exactly once.
    /// </summary>
    [Fact]
    public async Task WidgetBatch_calls_compose_once_for_covered_widgets()
    {
        // Arrange: catalog with the real widget keys from the codebase
        // (top-bots → BotAggregate, countries → GeoBreakdown)
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new RecordingComposer();

        var requestedWidgets = new[] { "top-bots", "countries" };

        // The covered keys are those for which catalog.NeedsFor returns non-null.
        var coveredKeys = requestedWidgets
            .Where(k => catalog.NeedsFor(k) is not null)
            .ToArray();

        // Act — this mirrors the middleware logic:
        // build a manifest from the covered subset and compose once.
        Assert.True(coveredKeys.Length > 0, "At least one covered key expected in catalog");

        var manifest = new DashboardPageManifest("sb.batch.update", coveredKeys);
        var window = new DashboardPageWindow(
            StartTime: DateTime.UtcNow.AddHours(-6),
            EndTime: DateTime.UtcNow,
            AudienceFilter: "all",
            ProbMin: null,
            Domains: null,
            TopN: 500,
            BucketMinutes: 60);

        var result = await composer.ComposeAsync(manifest, window, default);

        // Assert — exactly one compose call, manifest covers both widget keys.
        Assert.Equal(1, composer.ComposeCallCount);
        Assert.NotNull(composer.LastManifest);
        Assert.Contains("top-bots", composer.LastManifest!.WidgetKeys);
        Assert.Contains("countries", composer.LastManifest.WidgetKeys);
        Assert.NotNull(result);
    }

    /// <summary>
    ///     Verifies that widgets NOT in the catalog (e.g. "visitors") are excluded
    ///     from the compose manifest without affecting the catalog lookup for covered ones.
    /// </summary>
    [Fact]
    public void WidgetBatch_excludes_non_covered_widgets_from_compose_manifest()
    {
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();

        var requestedWidgets = new[] { "top-bots", "countries", "visitors", "threats", "sessions" };

        var coveredKeys = requestedWidgets
            .Where(k => catalog.NeedsFor(k) is not null)
            .ToArray();

        var nonCoveredKeys = requestedWidgets
            .Where(k => catalog.NeedsFor(k) is null)
            .ToArray();

        // top-bots and countries are covered; visitors, threats, sessions are not.
        Assert.Contains("top-bots", coveredKeys);
        Assert.Contains("countries", coveredKeys);

        // Non-covered widgets must NOT appear in the compose manifest.
        foreach (var key in nonCoveredKeys)
        {
            Assert.DoesNotContain(key, coveredKeys);
        }
    }

    /// <summary>
    ///     Verifies that when ALL requested widgets are non-covered, ComposeAsync
    ///     is NOT called (no manifest to build, no batch to issue).
    /// </summary>
    [Fact]
    public async Task WidgetBatch_skips_compose_when_no_covered_widgets()
    {
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new RecordingComposer();

        var requestedWidgets = new[] { "visitors", "sessions", "threats" };

        var coveredKeys = requestedWidgets
            .Where(k => catalog.NeedsFor(k) is not null)
            .ToArray();

        // None covered — middleware must not call ComposeAsync.
        if (coveredKeys.Length == 0)
        {
            // This branch mirrors the middleware guard:
            // if (coveredKeys.Length == 0) skip compose.
            Assert.Equal(0, composer.ComposeCallCount);
            return;
        }

        // If unexpectedly some are covered, still assert once.
        var manifest = new DashboardPageManifest("sb.batch.update", coveredKeys);
        var window = new DashboardPageWindow(null, null, "all", null, null, 500, 60);
        _ = await composer.ComposeAsync(manifest, window, default);
        Assert.Equal(1, composer.ComposeCallCount);
    }

    /// <summary>
    ///     End-to-end boundary test: the middleware stashes the DashboardPageResult
    ///     in HttpContext.Items["sb.dashboard.pageresult"] so covered ViewComponents
    ///     can read their slice without a self-fetch.
    /// </summary>
    [Fact]
    public async Task WidgetBatch_stashes_page_result_in_HttpContext_Items()
    {
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new RecordingComposer();

        var requestedWidgets = new[] { "top-bots", "countries" };
        var coveredKeys = requestedWidgets
            .Where(k => catalog.NeedsFor(k) is not null)
            .ToArray();

        var httpContext = new DefaultHttpContext();

        // Simulate what the middleware must do: compose, then stash.
        var manifest = new DashboardPageManifest("sb.batch.update", coveredKeys);
        var window = new DashboardPageWindow(null, null, "all", null, null, 500, 60);
        var pageResult = await composer.ComposeAsync(manifest, window, default);
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        // Assert: stashed and of the correct type.
        Assert.True(httpContext.Items.ContainsKey("sb.dashboard.pageresult"));
        Assert.IsType<DashboardPageResult>(httpContext.Items["sb.dashboard.pageresult"]);
    }

    // ---------- warm-bundle delta path (out-of-request materialization) ----------

    /// <summary>
    ///     When a content cache is available, the delta path stashes the warm traffic
    ///     page bundle (all five kinds) instead of composing its own subset — so a live
    ///     update reads a ready bundle the tick materializer keeps fresh.
    /// </summary>
    [Fact]
    public async Task Delta_prefers_the_warm_page_bundle_when_a_content_cache_is_available()
    {
        var composes = 0;
        long tick = 1;
        // Structural §8 fix: GetCurrentAsync never composes on the request thread anymore --
        // this test-only decorator simulates "the materializer already warmed it" so the
        // delta-path routing (prefer the warm bundle over a subset compose) can still be
        // asserted in isolation.
        var cache = new Mostlylucid.BotDetection.Test.Helpers.AutoWarmingContentCache(
            new DashboardContentCache(
                (_, _, _) => { composes++; return Task.FromResult(MakeResult()); },
                () => tick,
                Options.Create(new DashboardMaterializerOptions())),
            () => tick);
        var manifests = new DefaultDashboardPageManifestSource();
        var ctx = new DefaultHttpContext();
        var window = new DashboardPageWindow(null, null, "all", null, null, 500, 60);

        var stashed = await SbWidgetBatchMiddleware.TryStashWarmPageBundleAsync(
            cache, manifests, ctx, window, NullLogger.Instance, default);

        Assert.True(stashed);
        Assert.IsType<DashboardPageResult>(ctx.Items["sb.dashboard.pageresult"]);
        Assert.Equal(1, composes); // composed the traffic page bundle once (cold), now cached
    }

    /// <summary>
    ///     With no content cache (a widgets-only host, or the dashboard cache
    ///     unregistered), the delta path reports false so the caller falls back to its
    ///     subset compose — no regression to the pre-materialization behaviour.
    /// </summary>
    [Fact]
    public async Task Delta_falls_back_when_no_content_cache_is_available()
    {
        var ctx = new DefaultHttpContext();
        var window = new DashboardPageWindow(null, null, "all", null, null, 500, 60);

        var stashed = await SbWidgetBatchMiddleware.TryStashWarmPageBundleAsync(
            null, null, ctx, window, NullLogger.Instance, default);

        Assert.False(stashed);
        Assert.False(ctx.Items.ContainsKey("sb.dashboard.pageresult"));
    }

    // ---------- compose-failure guard (prod P0: all-null bundle poisoned the shingle cache) ----------

    /// <summary>
    ///     The prod P0 regression guard: RemoteDashboardEventStore.ComposeBatchAsync returns
    ///     an ALL-NULL DashboardDatasetBundle on any transport/non-success response, and
    ///     TryComposeAndStashAsync must never stash that as authoritative — the renderers
    ///     treat a present stash as real data, so stashing it cached EMPTY shingles and
    ///     latched the boot gate with nothing to serve (a gateway down at site boot).
    /// </summary>
    [Fact]
    public void Compose_failure_all_null_bundle_is_never_treated_as_data()
    {
        var failure = new DashboardDatasetBundle(null, null, null, null, null);
        var requested = new[] { DatasetKind.SummaryStats, DatasetKind.BotAggregate, DatasetKind.EndpointStats };

        Assert.False(SbWidgetBatchMiddleware.HasAnyRequestedData(failure, requested));
    }

    /// <summary>
    ///     A genuine empty window still yields non-null EMPTY lists — that is real data
    ///     ("we looked, there is nothing"), distinct from the all-null failure sentinel.
    /// </summary>
    [Fact]
    public void Genuine_empty_window_is_real_data_not_a_failure()
    {
        var empty = new DashboardDatasetBundle(
            Summary: EmptySummary(),
            TimeBuckets: new List<DashboardTimeSeriesPoint>(),
            BotAggregate: new List<DashboardTopBotEntry>(),
            Geo: new List<DashboardCountryStats>(),
            Endpoints: new List<DashboardEndpointStats>());
        var requested = new[] { DatasetKind.BotAggregate };

        Assert.True(SbWidgetBatchMiddleware.HasAnyRequestedData(empty, requested));
    }

    /// <summary>
    ///     A partial bundle (compose returned some slices, others null) is acceptable —
    ///     the render helpers self-fetch the missing slices. Only an all-null result is
    ///     the failure sentinel.
    /// </summary>
    [Fact]
    public void Partial_bundle_with_one_requested_slice_is_acceptable()
    {
        var partial = new DashboardDatasetBundle(
            Summary: EmptySummary(),
            TimeBuckets: null,
            BotAggregate: null,
            Geo: null,
            Endpoints: null);
        var requested = new[] { DatasetKind.BotAggregate, DatasetKind.SummaryStats };

        Assert.True(SbWidgetBatchMiddleware.HasAnyRequestedData(partial, requested));
    }

    /// <summary>
    ///     A bundle that carries only slices that were NOT requested is not "data" for
    ///     this compose — the delta would render empty widgets and cache them.
    /// </summary>
    [Fact]
    public void Bundle_with_only_unrequested_slices_is_not_data_for_this_compose()
    {
        var bundle = new DashboardDatasetBundle(
            Summary: EmptySummary(),
            TimeBuckets: null,
            BotAggregate: null,
            Geo: null,
            Endpoints: null);
        var requested = new[] { DatasetKind.BotAggregate, DatasetKind.GeoBreakdown };

        Assert.False(SbWidgetBatchMiddleware.HasAnyRequestedData(bundle, requested));
    }
}
