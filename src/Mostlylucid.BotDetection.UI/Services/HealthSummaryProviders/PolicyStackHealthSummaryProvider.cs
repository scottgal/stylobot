using System.Globalization;
using Mostlylucid.BotDetection.UI.Models;

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

    public PolicyStackHealthSummaryProvider(PolicyStackSummaryBuilder? builder = null)
    {
        _builder = builder;
    }

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        if (_builder is null) return null;

        var summary = await _builder.BuildAsync(ct).ConfigureAwait(false);
        if (summary is null) return null;

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
            DrillLabel: "View rules");
    }
}
