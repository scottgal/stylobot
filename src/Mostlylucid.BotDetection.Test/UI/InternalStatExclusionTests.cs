using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Acceptance tests for Task 7, Step 3 (#34 stat-exclusion gap).
///
///     Two surfaces are covered:
///
///     1. <see cref="SignatureAggregateCache.GetCounts"/> (in-memory) - excludes
///        <see cref="BotType.Internal"/> from All / Bots / Humans chips.
///
///     2. <see cref="SqliteDashboardEventStore.GetSummaryAsync"/> and
///        <see cref="SqliteDashboardEventStore.GetTimeSeriesAsync"/> (SQL) - the
///        <c>detections</c> table query now carries <c>AND bot_type IS NOT 'Internal'</c>
///        so health-probe / LAN traffic does not inflate TotalRequests in the
///        Traffic chart and Summary strip.
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

    // ── SQL-level exclusion (#34 fix): GetSummaryAsync + GetTimeSeriesAsync ──

    private static DashboardDetectionEvent MakeSqlDetection(
        string sig, string botType, bool isBot, double probability, DateTime? at = null) => new()
    {
        RequestId      = Guid.NewGuid().ToString("N")[..12],
        Timestamp      = at ?? DateTime.UtcNow,
        IsBot          = isBot,
        BotProbability = probability,
        BotType        = botType,
        BotName        = botType == "Internal" ? "Health Probe" : "Test Bot",
        RiskBand       = isBot ? "High" : "Low",
        Confidence     = 0.9,
        Method         = "GET",
        Path           = "/health",
        StatusCode     = 200,
        PrimarySignature = sig,
    };

    /// <summary>
    ///     GetSummaryAsync must exclude Internal detections from TotalRequests /
    ///     BotRequests / HumanRequests (the Traffic-strip KPIs).
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_ExcludesInternal_FromTotalRequests()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-internal-excl");

        // One health probe (Internal), one public bot.
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-probe", "Internal",     isBot: false, probability: 0.05));
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-bot",   "SearchEngine", isBot: true,  probability: 0.95));

        var summary = await fx.Store.GetSummaryAsync(
            startTime: DateTime.UtcNow.AddHours(-1),
            endTime:   DateTime.UtcNow.AddHours(1));

        // Only the public bot must appear.
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(1, summary.BotRequests);
        Assert.Equal(0, summary.HumanRequests);
    }

    /// <summary>
    ///     GetSummaryAsync must not accidentally drop non-Internal traffic when
    ///     there are no Internal rows in the window (regression guard).
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_NoInternal_TotalEqualsAllRows()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-no-internal");

        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-bot",   "SearchEngine", isBot: true,  probability: 0.95));
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-human", "Human",        isBot: false, probability: 0.05));

        var summary = await fx.Store.GetSummaryAsync(
            startTime: DateTime.UtcNow.AddHours(-1),
            endTime:   DateTime.UtcNow.AddHours(1));

        Assert.Equal(2, summary.TotalRequests);
    }

    /// <summary>
    ///     GetTimeSeriesAsync bucket totals must not count Internal detections.
    /// </summary>
    [Fact]
    public async Task GetTimeSeriesAsync_ExcludesInternal_FromBucketTotals()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("timeseries-internal-excl");

        var now = DateTime.UtcNow;
        // One Internal, one public bot — both in the query window.
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-probe", "Internal",     isBot: false, probability: 0.05, at: now.AddMinutes(-5)));
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-bot",   "SearchEngine", isBot: true,  probability: 0.95, at: now.AddMinutes(-5)));

        var series = await fx.Store.GetTimeSeriesAsync(
            startTime:  now.AddHours(-1),
            endTime:    now.AddHours(1),
            bucketSize: TimeSpan.FromHours(2));

        var totalAcrossBuckets = series.Sum(p => p.TotalCount);

        // Only the public bot row must be counted.
        Assert.Equal(1, totalAcrossBuckets);
    }

    /// <summary>
    ///     GetTimeSeriesAsync must not drop non-Internal traffic when there are
    ///     no Internal rows (regression guard).
    /// </summary>
    [Fact]
    public async Task GetTimeSeriesAsync_NoInternal_TotalEqualsAllRows()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("timeseries-no-internal");

        var now = DateTime.UtcNow;
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-bot",   "SearchEngine", isBot: true,  probability: 0.95, at: now.AddMinutes(-5)));
        await fx.Store.AddDetectionAsync(MakeSqlDetection("sig-human", "Human",        isBot: false, probability: 0.05, at: now.AddMinutes(-5)));

        var series = await fx.Store.GetTimeSeriesAsync(
            startTime:  now.AddHours(-1),
            endTime:    now.AddHours(1),
            bucketSize: TimeSpan.FromHours(2));

        var totalAcrossBuckets = series.Sum(p => p.TotalCount);
        Assert.Equal(2, totalAcrossBuckets);
    }
}
