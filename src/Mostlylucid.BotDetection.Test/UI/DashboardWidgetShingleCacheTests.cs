using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Middleware;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The dashboard widget shingle cache is a bounded LFU of per-widget rendered
///     OOB elements, keyed by a fingerprint = widget + filter/params + the widget's
///     data-change version (the change-cursor tick for that widget's surface). These
///     tests pin the fingerprint's granularity (a data change or a filter change
///     re-keys ONLY the affected widget) and the cache's serve/store behaviour.
/// </summary>
public sealed class DashboardWidgetShingleCacheTests
{
    private static IQueryCollection Q(params (string Key, string Value)[] kv) =>
        new QueryCollection(kv.ToDictionary(p => p.Key, p => new StringValues(p.Value)));

    [Fact]
    public void Fingerprint_changes_when_version_changes()
    {
        // A data change on the widget's surface bumps the cursor -> a new version ->
        // a fresh fingerprint, so the stale shingle is not served.
        var a = WidgetRenderHelpers.ComputeWidgetShingleFingerprint("summary", Q(), 5);
        var b = WidgetRenderHelpers.ComputeWidgetShingleFingerprint("summary", Q(), 6);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_changes_when_params_change()
    {
        // A filter change is a new content shape for THIS widget only.
        var a = WidgetRenderHelpers.ComputeWidgetShingleFingerprint("topbots", Q(("filter", "bots")), 5);
        var b = WidgetRenderHelpers.ComputeWidgetShingleFingerprint("topbots", Q(("filter", "humans")), 5);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_stable_for_same_widget_params_version()
    {
        // Same widget + same filter + no data change -> same fingerprint -> the LFU
        // serves the resident shingle warm (no re-render).
        var a = WidgetRenderHelpers.ComputeWidgetShingleFingerprint("summary", Q(("window", "24h")), 7);
        var b = WidgetRenderHelpers.ComputeWidgetShingleFingerprint("summary", Q(("window", "24h")), 7);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WidgetSurface_maps_threats_distinctly_from_signature_and_summary()
    {
        // Distinct surfaces => a bump on one leaves the others' versions (and thus
        // their shingles) untouched. This is what makes "change one widget re-keys
        // JUST that widget" true.
        Assert.Equal("threats", WidgetRenderHelpers.WidgetSurface("threats"));
        Assert.Equal("signature", WidgetRenderHelpers.WidgetSurface("overview-topbots"));
        Assert.Equal("summary", WidgetRenderHelpers.WidgetSurface("summary"));
        Assert.Equal("countries", WidgetRenderHelpers.WidgetSurface("countries"));
    }

    [Fact]
    public void Cache_stores_then_serves_the_shingle()
    {
        var cache = new DashboardWidgetShingleCache(
            Options.Create(new StyloBotDashboardOptions { WidgetShingleCacheMaxEntries = 8 }));

        cache.Set("fp-1", "<div hx-swap-oob=\"morph\">1</div>");

        Assert.True(cache.TryGet("fp-1", out var html));
        Assert.Equal("<div hx-swap-oob=\"morph\">1</div>", html);
        Assert.False(cache.TryGet("fp-absent", out _));
    }

    [Fact]
    public void Cache_is_bounded_and_self_trims()
    {
        var cache = new DashboardWidgetShingleCache(
            Options.Create(new StyloBotDashboardOptions { WidgetShingleCacheMaxEntries = 4 }));

        // Push well past the bound: the LFU keeps it bounded (cold entries evict).
        for (var i = 0; i < 50; i++)
            cache.Set($"fp-{i}", $"<div>{i}</div>");

        Assert.True(cache.Count <= 4, $"expected <= 4 resident shingles, was {cache.Count}");
    }
}
