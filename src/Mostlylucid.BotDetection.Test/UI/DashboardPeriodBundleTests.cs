using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Locks the period-selector read contract: reading an envelope for a given window
///     token serves THAT period's bundle — never the default period's. The 2026-08-12
///     "changing the period does nothing" defect class was exactly this: the swap URLs baked
///     the SSR-time window, so the new period's read resolved the old period's envelope.
///     With the cache keyed per (manifest, window), two windows must resolve to two bundles.
/// </summary>
public sealed class DashboardPeriodBundleTests
{
    private static readonly DefaultDashboardPageManifestSource ManifestSource = new();

    [Fact]
    public async Task Two_windows_resolve_to_two_distinct_bundles()
    {
        // The store reports TotalRequests == the window span in hours, so a crossed read
        // (6h request getting the 24h bundle) is immediately visible as the wrong number.
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.ComposeBatchAsync(
                It.IsAny<DashboardBatchRequest>(), It.IsAny<CancellationToken>()))
            .Returns((DashboardBatchRequest r, CancellationToken _) =>
            {
                var hours = (int)((r.EndTime!.Value - r.StartTime!.Value).TotalHours);
                return Task.FromResult(new DashboardDatasetBundle(
                    Summary: new DashboardSummary
                    {
                        Timestamp = DateTime.UtcNow,
                        TotalRequests = hours,
                        BotRequests = 0,
                        HumanRequests = hours,
                        UncertainRequests = 0,
                        UniqueSignatures = hours,
                        RiskBandCounts = new(),
                        TopBotTypes = new(),
                        TopActions = new()
                    },
                    TimeBuckets: null, BotAggregate: null, Geo: null, Endpoints: null));
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDashboardChangeCursor>(new DashboardChangeCursor());
        services.AddSingleton<IDashboardEventStore>(store.Object);
        services.AddScoped<IDashboardPageComposer, DefaultDashboardPageComposer>();
        services.AddSingleton(DashboardWidgetCatalog.BuildFromLoadedAssemblies());
        services.AddDashboardMaterialization(runTickMaterializer: false);
        await using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IDashboardContentCache>();
        var manifest = ManifestSource.For("dashboard.traffic")!;
        var now = DateTime.UtcNow;
        var window6h = DashboardRoutingHelpers.BuildPinnedWindow("6h", now);
        var window24h = DashboardRoutingHelpers.BuildPinnedWindow("24h", now);

        await cache.WarmAsync(manifest, window6h, tick: 1, CancellationToken.None);
        await cache.WarmAsync(manifest, window24h, tick: 2, CancellationToken.None);

        var read6h = await cache.GetCurrentAsync(manifest, window6h, CancellationToken.None);
        var read24h = await cache.GetCurrentAsync(manifest, window24h, CancellationToken.None);

        Assert.False(read6h.IsWarming);
        Assert.False(read24h.IsWarming);
        Assert.Equal(6, read6h.Summary!.TotalRequests);
        Assert.Equal(24, read24h.Summary!.TotalRequests);
    }
}
