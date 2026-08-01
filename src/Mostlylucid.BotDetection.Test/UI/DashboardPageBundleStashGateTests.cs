using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression guard for the /dashboard/traffic "No endpoint analytics yet" P0:
///     <see cref="StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash"/> is the
///     gate that decides whether a composed <see cref="DashboardPageResult"/> is complete
///     enough to stash as authoritative (vs. falling back to direct event-store reads). It
///     validated Summary/BotAggregate/Geo but never Endpoints, so a compose where every
///     OTHER slice populated but Endpoints came back empty still got stashed as the real
///     answer -- and once stashed, <c>pageResult?.Endpoints is { }</c> at the render layer
///     treats a non-null empty list as "available", never falling back. Permanent empty
///     widget on the operator's #1 page.
/// </summary>
public sealed class DashboardPageBundleStashGateTests
{
    private static DashboardPageResult NewResult(
        DashboardSummary? summary,
        IReadOnlyList<DashboardTopBotEntry>? botAggregate,
        IReadOnlyList<DashboardCountryStats>? geo,
        IReadOnlyList<DashboardEndpointStats>? endpoints) =>
        new(new DashboardDatasetBundle(summary, null, botAggregate, geo, endpoints));

    private static DashboardSummary AnySummary() => new()
    {
        Timestamp = DateTime.UtcNow,
        TotalRequests = 1,
        BotRequests = 1,
        HumanRequests = 0,
        UncertainRequests = 0,
        UniqueSignatures = 1,
        RiskBandCounts = new(),
        TopBotTypes = new(),
        TopActions = new()
    };

    private static List<DashboardTopBotEntry> PopulatedBotAggregate() =>
        [new() { PrimarySignature = "sig", BotName = "bot", HitCount = 1, BotProbability = 0.1, LastSeen = DateTime.UtcNow }];

    private static List<DashboardCountryStats> PopulatedGeo() =>
        [new() { CountryCode = "GB", TotalCount = 1, BotCount = 0 }];

    private static List<DashboardEndpointStats> PopulatedEndpoints() =>
        [new() { Method = "GET", Path = "/", TotalCount = 1, LastSeen = DateTime.UtcNow }];

    [Fact]
    public void FullyPopulatedBundle_IsCompleteEnoughToStash()
    {
        var result = NewResult(AnySummary(), PopulatedBotAggregate(), PopulatedGeo(), PopulatedEndpoints());

        Assert.True(StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash(result));
    }

    [Fact]
    public void EmptyEndpoints_WithEveryOtherSlicePopulated_IsNotCompleteEnoughToStash()
    {
        // The exact P0 shape: Summary/BotAggregate/Geo all real, Endpoints came back empty.
        var result = NewResult(AnySummary(), PopulatedBotAggregate(), PopulatedGeo(), endpoints: []);

        Assert.False(StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash(result));
    }

    [Fact]
    public void NullEndpoints_WithEveryOtherSlicePopulated_IsNotCompleteEnoughToStash()
    {
        var result = NewResult(AnySummary(), PopulatedBotAggregate(), PopulatedGeo(), endpoints: null);

        Assert.False(StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash(result));
    }

    [Fact]
    public void EmptyBotAggregate_StillRejected_PreExistingCheckUnchanged()
    {
        var result = NewResult(AnySummary(), botAggregate: [], PopulatedGeo(), PopulatedEndpoints());

        Assert.False(StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash(result));
    }

    [Fact]
    public void NullSummary_StillRejected_PreExistingCheckUnchanged()
    {
        var result = NewResult(summary: null, PopulatedBotAggregate(), PopulatedGeo(), PopulatedEndpoints());

        Assert.False(StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash(result));
    }
}
