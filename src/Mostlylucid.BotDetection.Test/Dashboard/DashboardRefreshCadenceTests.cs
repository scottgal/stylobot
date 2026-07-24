using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     TDD for <see cref="DashboardRefreshCadence.ComputeEffectiveIntervalSeconds"/> --
///     the pure function folding THE NAMED INVARIANT (MIN over touched freshness
///     classes), LFU-hotness scaling, the global floor, and the adaptive scale factor
///     into one effective per-page-key refresh interval. See
///     <see cref="DashboardMaterializerAdaptiveControllerTests"/> for the adaptive
///     controller that produces the scale factor consumed here.
/// </summary>
public sealed class DashboardRefreshCadenceTests
{
    private static DashboardMaterializerOptions Options() => new()
    {
        GlobalMinIntervalSeconds = 60,
        AggregateBaseIntervalSeconds = 300,
        LiveBaseIntervalSeconds = 60,
    };

    // ---- THE NAMED INVARIANT: this is the single most important test in this file. ----

    [Fact]
    public void SharedEntry_TouchingBothFreshnessClasses_UsesTheFasterIntervalNeverTheSlower()
    {
        // dashboard.traffic's real shape: mostly Aggregate widget keys, plus "top-bots"
        // (Live -- it also backs the Visitors row). A shared cache entry must NEVER serve
        // staler than the fastest thing it satisfies: effective cadence = MIN(intervals of
        // everything it serves), so this must resolve to LiveBaseIntervalSeconds (60s),
        // never AggregateBaseIntervalSeconds (300s) and never some average of the two.
        var sharedManifest = new DashboardPageManifest(
            "dashboard.traffic",
            new[] { "summary", "time-chart", "top-bots", "countries", "endpoints", "site-health" });
        var aggregateOnlyManifest = new DashboardPageManifest(
            "dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();

        // Cold (accessCount 0) and unthrottled (scale 1.0) isolates the invariant from the
        // other two knobs (hotness scaling, adaptive stretch) -- both entries get identical
        // treatment on those axes, so any difference in the result is attributable ONLY to
        // which freshness classes each manifest's bundle touches.
        var sharedInterval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(
            sharedManifest, accessCount: 0, adaptiveScaleFactor: 1.0, options);
        var aggregateOnlyInterval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(
            aggregateOnlyManifest, accessCount: 0, adaptiveScaleFactor: 1.0, options);

        Assert.Equal(options.LiveBaseIntervalSeconds, sharedInterval);
        Assert.NotEqual(options.AggregateBaseIntervalSeconds, sharedInterval);
        Assert.True(sharedInterval < aggregateOnlyInterval,
            "a page key bundling a Live-class row alongside Aggregate ones must refresh " +
            "no slower than a page key that is purely Aggregate -- collapsing rows onto one " +
            "cache entry is only safe because the entry ticks at the FASTEST need.");
    }

    [Fact]
    public void Pure_Live_only_manifest_uses_the_Live_base_interval()
    {
        var manifest = new DashboardPageManifest("dashboard.topbots", new[] { DashboardRowWidgetKeys.TopBotsRaw });
        var options = Options();

        var interval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 1.0, options);

        Assert.Equal(options.LiveBaseIntervalSeconds, interval);
    }

    [Fact]
    public void Pure_Aggregate_only_manifest_uses_the_Aggregate_base_interval()
    {
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();

        var interval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 1.0, options);

        Assert.Equal(options.AggregateBaseIntervalSeconds, interval);
    }

    // ---- LFU-hotness scaling ----

    [Fact]
    public void Hotter_page_key_refreshes_closer_to_the_floor_than_a_cold_one()
    {
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();

        var cold = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, accessCount: 0, adaptiveScaleFactor: 1.0, options);
        var hot = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, accessCount: 1000, adaptiveScaleFactor: 1.0, options);

        Assert.Equal(options.AggregateBaseIntervalSeconds, cold); // never-accessed: full base interval, unscaled.
        Assert.True(hot < cold, "a frequently-read page key should drift toward the floor, not stay at the base interval");
        Assert.True(hot >= options.GlobalMinIntervalSeconds, "hotness scaling must never itself violate the global floor");
    }

    [Fact]
    public void Hotness_scaling_never_produces_an_interval_below_the_global_floor()
    {
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();

        // Extreme hotness should asymptotically approach the floor, never cross it.
        var interval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, accessCount: int.MaxValue, adaptiveScaleFactor: 1.0, options);

        Assert.True(interval >= options.GlobalMinIntervalSeconds);
    }

    // ---- Global floor ----

    [Fact]
    public void Global_floor_wins_even_for_a_Live_class_key_with_extreme_hotness_and_no_adaptive_stretch()
    {
        var manifest = new DashboardPageManifest("dashboard.topbots", new[] { DashboardRowWidgetKeys.TopBotsRaw });
        var opts = Options();
        opts.LiveBaseIntervalSeconds = 60;
        opts.GlobalMinIntervalSeconds = 60;

        var interval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, accessCount: 999_999, adaptiveScaleFactor: 1.0, opts);

        Assert.Equal(opts.GlobalMinIntervalSeconds, interval);
    }

    [Fact]
    public void Global_floor_is_never_violated_regardless_of_a_below_floor_base_interval_misconfiguration()
    {
        // Defensive: if an operator misconfigures a base interval below the floor, the floor
        // must still win -- the floor is the one hard invariant, not just a default.
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();
        options.AggregateBaseIntervalSeconds = 10;
        options.GlobalMinIntervalSeconds = 60;

        var interval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 1.0, options);

        Assert.True(interval >= 60);
    }

    // ---- Adaptive scale factor ----

    [Fact]
    public void Adaptive_scale_factor_stretches_the_interval_multiplicatively()
    {
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();

        var unscaled = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 1.0, options);
        var scaled = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 2.0, options);

        Assert.Equal(unscaled * 2, scaled);
    }

    [Fact]
    public void Adaptive_scale_factor_below_one_is_clamped_to_one_never_speeds_up_past_the_base()
    {
        // The adaptive controller only ever slows things down under pressure; a caller
        // accidentally passing a sub-1.0 factor must not accelerate refresh below the
        // class/hotness-derived value.
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var options = Options();

        var normal = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 1.0, options);
        var withBadFactor = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 0.1, options);

        Assert.Equal(normal, withBadFactor);
    }

    [Fact]
    public void Empty_widget_key_manifest_fails_safe_to_the_Live_base_interval()
    {
        var manifest = new DashboardPageManifest("dashboard.empty", Array.Empty<string>());
        var options = Options();

        var interval = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(manifest, 0, 1.0, options);

        Assert.Equal(options.LiveBaseIntervalSeconds, interval);
    }
}
