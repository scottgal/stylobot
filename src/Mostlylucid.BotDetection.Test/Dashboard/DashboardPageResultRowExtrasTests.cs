using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Stage 2a: <see cref="DashboardPageResult"/> widened to also carry the raw
///     (unshaped) datasets for the rows that don't come from
///     <c>IDashboardEventStore.ComposeBatchAsync</c> (Clusters/TopBots/Sessions/Threats
///     are sourced from other services -- <c>IBotClusterReader</c>,
///     <c>IDetectionArchive</c> -- so they ride alongside the existing 5-property
///     dataset-kind bundle rather than through <see cref="DatasetKind"/>).
///     Each new slot follows the exact same "null when not composed" contract as the
///     existing Summary/TimeBuckets/BotAggregate/Geo/Endpoints properties.
/// </summary>
public sealed class DashboardPageResultRowExtrasTests
{
    private static DashboardDatasetBundle EmptyBundle() => new(null, null, null, null, null);

    [Fact]
    public void New_row_slices_are_null_when_not_supplied()
    {
        var result = new DashboardPageResult(EmptyBundle());

        Assert.Null(result.ClustersRaw);
        Assert.Null(result.ClusterDiagnosticsRaw);
        Assert.Null(result.TopBotsRaw);
        Assert.Null(result.SessionsRaw);
        Assert.Null(result.SessionsRawTotalCount);
        Assert.Null(result.ThreatsRaw);
    }

    [Fact]
    public void New_row_slices_round_trip_when_supplied()
    {
        var clusters = new List<ClusterViewModel>
        {
            new() { ClusterId = "c1", Label = "Cluster 1", Type = "Network" }
        };
        var diagnostics = new ClusterDiagnosticsViewModel { Algorithm = "leiden" };
        var topBots = new List<DashboardTopBotEntry> { new() { PrimarySignature = "sig1" } };
        var sessions = new List<SessionListEntry>
        {
            new() { Signature = "sig1", StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow, RequestCount = 1, DominantState = "PageView", IsBot = false, AvgBotProbability = 0.1, RiskBand = "Low" }
        };
        var threats = new List<ThreatEntry> { new() { Signature = "sig1", Path = "/wp-admin" } };

        var result = new DashboardPageResult(
            EmptyBundle(),
            clustersRaw: clusters,
            clusterDiagnosticsRaw: diagnostics,
            topBotsRaw: topBots,
            sessionsRaw: sessions,
            sessionsRawTotalCount: 1,
            threatsRaw: threats);

        Assert.Same(clusters, result.ClustersRaw);
        Assert.Same(diagnostics, result.ClusterDiagnosticsRaw);
        Assert.Same(topBots, result.TopBotsRaw);
        Assert.Same(sessions, result.SessionsRaw);
        Assert.Equal(1, result.SessionsRawTotalCount);
        Assert.Same(threats, result.ThreatsRaw);
    }

    [Fact]
    public void Warming_placeholder_still_reports_all_new_slices_as_null()
    {
        var warming = DashboardPageResult.Warming;

        Assert.True(warming.IsWarming);
        Assert.Null(warming.ClustersRaw);
        Assert.Null(warming.TopBotsRaw);
        Assert.Null(warming.SessionsRaw);
        Assert.Null(warming.ThreatsRaw);
    }
}
