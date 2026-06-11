using System.Globalization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

/// <summary>
///     Pack-health row tile for the global policy stack. Wraps
///     <see cref="PolicyStackSummaryBuilder.BuildAsync"/> so the dashboard
///     overview surfaces the live rules count + secondary observe/draft line
///     alongside the AspNet and meters tiles.
///     <para>
///         Leftmost tile (Order = 100) because policy is the headline: the
///         operator looking at the dashboard wants to see "is something
///         actually enforcing" before "how many endpoints am I inventorying".
///     </para>
///     <para>
///         Returns <c>null</c> when the summary builder itself returns null
///         (no <c>IPolicyRuleStore</c> on this host -- viewer-mode hosts hit
///         the gateway's <c>/api/v1/packs/policystack/summary</c> JSON for
///         their tile instead).
///     </para>
/// </summary>
public sealed class PolicyStackHealthSummaryProvider : IPackHealthSummaryProvider
{
    /// <summary>
    ///     Drill anchor for the tile footer. Targets the existing FOSS
    ///     /dashboard/policies row.
    /// </summary>
    public const string DrillPath = "/dashboard/policies";

    private readonly PolicyStackSummaryBuilder? _builder;
    private readonly PolicyStackSummaryCache? _cache;

    public PolicyStackHealthSummaryProvider(
        PolicyStackSummaryBuilder? builder = null,
        PolicyStackSummaryCache? cache = null)
    {
        _builder = builder;
        _cache = cache;
    }

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        if (_builder is null) return null;

        // Hit the centralised cache first. Invalidated by
        // DashboardFreshnessBridge on IPolicyRuleStore.Changed events --
        // not a private TTL. Per feedback_centralised_change_detection
        // every dashboard summary surface MUST share the one beacon path
        // so a fresh edit reaches the tile without per-builder warmups.
        var summary = _cache?.TryGet();
        if (summary is null)
        {
            summary = await _builder.BuildAsync(ct).ConfigureAwait(false);
            if (summary is null) return null;
            _cache?.Set(summary);
        }

        var value = summary.LiveRules.ToString(CultureInfo.InvariantCulture);
        var observe = summary.ObserveRules.ToString(CultureInfo.InvariantCulture);
        var draft = summary.DraftRules.ToString(CultureInfo.InvariantCulture);
        var delta = $"{observe} observe / {draft} draft";

        return new StatTileViewModel(
            Title: "Policy Stack",
            Value: value,
            Unit: "live",
            Delta: delta,
            HealthBand: summary.HealthBand,
            DrillHref: DrillPath,
            DrillLabel: "View rules",
            BeaconKey: DashboardFreshnessBeacon.Surfaces.PolicyStackSummary);
    }
}
