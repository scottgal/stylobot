using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Acceptance tests for Task 7, Step 3: stat exclusion #34.
///
///     <see cref="SignatureAggregateCache.GetCounts"/> must exclude
///     <see cref="BotType.Internal"/> entries (health probes, loopback traffic,
///     docker-bridge self-traffic) from the All / Bots / Humans counts that
///     drive the dashboard visitor-count chips.
///
///     The exclusion was introduced when Internal was first added to the widget
///     model (see SbWidgetBatchMiddleware.BuildTopBotsModel). This test pins the
///     contract: a health-probe row does NOT inflate the All chip, but IS
///     surfaced under the dedicated Internal chip count.
///
///     NOTE (#34 gap): <c>SqliteDashboardEventStore.GetSummaryAsync</c> and
///     <c>GetTimeSeriesAsync</c> do NOT yet filter <c>bot_type = 'Internal'</c>
///     from the <c>detections</c> table query, so health-probe requests inflate
///     TotalRequests in the Traffic chart and Summary strip. That is a separate
///     gap recorded in the task-7-report.md concerns section; it is not fixed here
///     because the controller must decide the approach (SQL predicate vs.
///     application-layer filter). See task-7-report.md for the exact call sites.
/// </summary>
public sealed class InternalStatExclusionTests
{
    private static DashboardDetectionEvent MakeDetection(
        string sig,
        string botType,
        bool isBot,
        double probability = 0.95) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = DateTime.UtcNow,
        IsBot = isBot,
        BotProbability = probability,
        BotType = botType,
        BotName = botType == "Internal" ? "Health Probe" : "Test Bot",
        RiskBand = "Low",
        Confidence = 0.9,
        Method = "GET",
        Path = "/health",
        PrimarySignature = sig,
    };

    /// <summary>
    ///     A health-probe entry (BotType = "Internal") in the signature cache must
    ///     NOT increment the All / Bots / Humans counters, but MUST appear in the
    ///     dedicated Internal counter.
    /// </summary>
    [Fact]
    public void GetCounts_ExcludesInternal_FromAllBotsHumans()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());

        // One health probe (Internal), one bot, one human.
        cache.UpdateFromDetection(MakeDetection("sig-health",  "Internal",      isBot: false, probability: 0.05));
        cache.UpdateFromDetection(MakeDetection("sig-bot",     "SearchEngine",  isBot: true,  probability: 0.95));
        cache.UpdateFromDetection(MakeDetection("sig-human",   "Human",         isBot: false, probability: 0.10));

        var counts = cache.GetCounts();

        // All must reflect only public (non-Internal) traffic.
        Assert.Equal(2, counts.All);
        Assert.Equal(1, counts.Bots);
        Assert.Equal(1, counts.Humans);
        // Internal is surfaced under its own counter, not inflating public counts.
        Assert.Equal(1, counts.Internal);
    }

    /// <summary>
    ///     Multiple health probes from different signatures (e.g. different
    ///     Kubernetes liveness probes hitting different pod IPs) all land in
    ///     Internal and do not inflate All.
    /// </summary>
    [Fact]
    public void GetCounts_MultipleInternalEntries_AllCountedInInternalOnly()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());

        cache.UpdateFromDetection(MakeDetection("sig-kube-1", "Internal", isBot: false, probability: 0.05));
        cache.UpdateFromDetection(MakeDetection("sig-kube-2", "Internal", isBot: false, probability: 0.05));
        cache.UpdateFromDetection(MakeDetection("sig-kube-3", "Internal", isBot: false, probability: 0.05));
        cache.UpdateFromDetection(MakeDetection("sig-bot",    "SearchEngine", isBot: true, probability: 0.92));

        var counts = cache.GetCounts();

        Assert.Equal(1, counts.All);    // only the search-engine bot
        Assert.Equal(1, counts.Bots);
        Assert.Equal(0, counts.Humans);
        Assert.Equal(3, counts.Internal);
    }

    /// <summary>
    ///     When there are no Internal entries, All / Bots / Humans cover all
    ///     signatures and Internal is zero. Regression: the exclusion must not
    ///     accidentally drop public traffic.
    /// </summary>
    [Fact]
    public void GetCounts_NoInternal_AllEqualsBotsAndHumans()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());

        cache.UpdateFromDetection(MakeDetection("sig-bot1", "SearchEngine", isBot: true));
        cache.UpdateFromDetection(MakeDetection("sig-bot2", "AiScraper",   isBot: true));
        cache.UpdateFromDetection(MakeDetection("sig-human", "Human",      isBot: false, probability: 0.05));

        var counts = cache.GetCounts();

        Assert.Equal(3, counts.All);
        Assert.Equal(2, counts.Bots);
        Assert.Equal(1, counts.Humans);
        Assert.Equal(0, counts.Internal);
    }
}
