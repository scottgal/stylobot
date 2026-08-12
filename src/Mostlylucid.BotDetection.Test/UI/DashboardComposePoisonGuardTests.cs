using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression for the 2026-08-12 prod incident: the remote store degrades compose
///     failures (gateway 401 / connection refused) to an ALL-NULL
///     <see cref="DashboardDatasetBundle"/>. The materializer stored that failure sentinel as
///     authoritative, so pages painted "0 traffic" until the next warm — lying zeros beside
///     real gateway data. The compose delegate now returns
///     <see cref="DashboardPageResult.Warming"/> when the composed page carries none of the
///     manifest's requested data, so readers render the honest warming state and the next
///     tick's warm retries. A genuinely empty window (non-null EMPTY lists) still caches.
/// </summary>
public sealed class DashboardComposePoisonGuardTests
{
    private static readonly DefaultDashboardPageManifestSource ManifestSource = new();
    private static readonly DashboardWidgetCatalog Catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();

    private static DashboardPageResult NullBundle() =>
        new(new DashboardDatasetBundle(null, null, null, null, null));

    private static DashboardPageResult RealTrafficPage() =>
        new(new DashboardDatasetBundle(
            Summary: AnySummary(),
            TimeBuckets:
            [
                new DashboardTimeSeriesPoint
                {
                    Timestamp = DateTime.UtcNow, BotCount = 0, HumanCount = 1, TotalCount = 1
                }
            ],
            BotAggregate: [new DashboardTopBotEntry { PrimarySignature = "sig", HitCount = 1 }],
            Geo: [new DashboardCountryStats { CountryCode = "GB", TotalCount = 1 }],
            Endpoints: [new DashboardEndpointStats { Method = "GET", Path = "/", TotalCount = 1 }]));

    private static DashboardPageResult GenuinelyEmptyTrafficPage() =>
        new(new DashboardDatasetBundle(
            Summary: AnySummary(),
            TimeBuckets: [],
            BotAggregate: [],
            Geo: [],
            Endpoints: []));

    private static DashboardSummary AnySummary() => new()
    {
        Timestamp = DateTime.UtcNow,
        TotalRequests = 1,
        BotRequests = 0,
        HumanRequests = 1,
        UncertainRequests = 0,
        UniqueSignatures = 1,
        RiskBandCounts = new(),
        TopBotTypes = new(),
        TopActions = new()
    };

    [Fact]
    public void AllNullBundle_for_traffic_manifest_is_not_stashable()
    {
        Assert.False(DefaultDashboardPageComposer.HasAnyRequestedData(
            NullBundle(), ManifestSource.For("dashboard.traffic")!, Catalog));
    }

    [Fact]
    public void RealBundle_for_traffic_manifest_is_stashable()
    {
        Assert.True(DefaultDashboardPageComposer.HasAnyRequestedData(
            RealTrafficPage(), ManifestSource.For("dashboard.traffic")!, Catalog));
    }

    [Fact]
    public void GenuinelyEmptyWindow_for_traffic_manifest_is_stashable()
    {
        // Honest zeros (non-null EMPTY lists) must still cache — only the null failure
        // sentinel is rejected, otherwise quiet periods render the warming spinner forever.
        Assert.True(DefaultDashboardPageComposer.HasAnyRequestedData(
            GenuinelyEmptyTrafficPage(), ManifestSource.For("dashboard.traffic")!, Catalog));
    }

    [Fact]
    public void SiteManifest_with_real_summary_is_stashable()
    {
        var page = new DashboardPageResult(new DashboardDatasetBundle(
            Summary: AnySummary(), TimeBuckets: null, BotAggregate: null, Geo: null,
            Endpoints: [new DashboardEndpointStats { Method = "GET", Path = "/", TotalCount = 1 }]));

        Assert.True(DefaultDashboardPageComposer.HasAnyRequestedData(
            page, ManifestSource.For("dashboard.site")!, Catalog));
    }

    [Fact]
    public void PureRowManifest_always_passes_the_guard()
    {
        // Row-extra manifests (topbots/clusters/sessions/threats) have no catalog kinds;
        // their slices can legitimately be null on hosts lacking the backing source, so
        // the guard must not reject them (their widgets fall back on their own).
        var page = new DashboardPageResult(new DashboardDatasetBundle(null, null, null, null, null));

        Assert.True(DefaultDashboardPageComposer.HasAnyRequestedData(
            page, ManifestSource.For("dashboard.topbots")!, Catalog));
    }

    [Fact]
    public async Task ContentCache_does_not_store_the_failure_sentinel()
    {
        // The full DI-delegate behavior: a compose that returns the all-null failure
        // sentinel must land as Warming in the cache — never as an authoritative
        // zero-data page — and a real compose must land as data.
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<IDashboardChangeCursor>(new DashboardChangeCursor());
        services.AddScoped<IDashboardPageComposer>(_ => new FakeComposer());
        services.AddSingleton(DashboardWidgetCatalog.BuildFromLoadedAssemblies());
        services.AddDashboardMaterialization(runTickMaterializer: false);
        await using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IDashboardContentCache>();
        var manifest = ManifestSource.For("dashboard.traffic")!;
        var window = DashboardRoutingHelpers.BuildPinnedWindow("24h", DateTime.UtcNow);

        // Tick 1: the compose fails with the all-null sentinel -> Warming, not zeros.
        await cache.WarmAsync(manifest, window, tick: 1, CancellationToken.None);
        var poisonedRead = await cache.GetCurrentAsync(manifest, window, CancellationToken.None);
        Assert.True(poisonedRead.IsWarming);

        // Tick 2: the compose succeeds -> the SAME envelope now serves real data.
        FakeComposer.Succeed = true;
        await cache.WarmAsync(manifest, window, tick: 2, CancellationToken.None);
        var healedRead = await cache.GetCurrentAsync(manifest, window, CancellationToken.None);
        Assert.False(healedRead.IsWarming);
        Assert.NotNull(healedRead.Summary);
    }

    private sealed class FakeComposer : IDashboardPageComposer
    {
        public static bool Succeed;

        public Task<DashboardPageResult> ComposeAsync(
            DashboardPageManifest manifest, DashboardPageWindow w, CancellationToken ct)
            => Task.FromResult(Succeed ? RealTrafficPage() : NullBundle());
    }
}
