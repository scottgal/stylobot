using System;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins <see cref="WidgetRenderHelpers.OverlayLiveHitTrend"/> — the fix for the sparkline
///     regression where every dashboard Top Bots render path read rows straight from the
///     DB-backed event store (HitTrend always empty there — the DB never stores per-minute
///     counts) without ever splicing in the live ring buffer from SignatureAggregateCache. The
///     write path (DetectionBroadcastMiddleware -> UpdateFromDetection) was always correct;
///     this pins the READ-side overlay that was missing from SbTopBotsViewComponent,
///     StyloBotDashboardMiddleware.BuildTopBotsModelFromRaw, and SbWidgetBatchMiddleware.BuildTopBotsModel.
/// </summary>
public class WidgetRenderHelpersOverlayLiveHitTrendTests
{
    private static DashboardTopBotEntry Row(string sig, int[]? hitTrend = null) => new()
    {
        PrimarySignature = sig,
        BotName = "Googlebot",
        HitCount = 42,
        BotProbability = 0.95,
        LastSeen = DateTime.UtcNow,
        HitTrend = hitTrend ?? Array.Empty<int>(),
    };

    private static DashboardDetectionEvent MakeDetection(string primarySignature, DateTime ts) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = ts,
        IsBot = true,
        BotProbability = 0.95,
        BotName = "Googlebot",
        BotType = "SearchEngine",
        PrimarySignature = primarySignature,
        RiskBand = "High",
        Confidence = 0.9,
        Method = "GET",
        Path = "/",
    };

    [Fact]
    public void Empty_HitTrend_row_is_patched_from_a_live_cache_entry()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        cache.UpdateFromDetection(MakeDetection("sig-live", DateTime.UtcNow));

        var result = WidgetRenderHelpers.OverlayLiveHitTrend(new[] { Row("sig-live") }, cache);

        Assert.NotEmpty(result[0].HitTrend);
    }

    [Fact]
    public void Row_with_no_matching_cache_entry_stays_empty_and_does_not_throw()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        // Cache has an entry for a DIFFERENT signature only.
        cache.UpdateFromDetection(MakeDetection("sig-other", DateTime.UtcNow));

        var result = WidgetRenderHelpers.OverlayLiveHitTrend(new[] { Row("sig-unknown") }, cache);

        Assert.Empty(result[0].HitTrend);
    }

    [Fact]
    public void Row_that_already_has_a_HitTrend_is_left_untouched()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        cache.UpdateFromDetection(MakeDetection("sig-live", DateTime.UtcNow));
        var preFilled = new[] { 1, 2, 3 };

        var result = WidgetRenderHelpers.OverlayLiveHitTrend(new[] { Row("sig-live", preFilled) }, cache);

        Assert.Same(preFilled, result[0].HitTrend);
    }

    [Fact]
    public void Null_signature_cache_returns_rows_unchanged()
    {
        var rows = new[] { Row("sig-any") };

        var result = WidgetRenderHelpers.OverlayLiveHitTrend(rows, signatureCache: null);

        Assert.Same(rows, result);
    }
}
