using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Donut variant of the bot-family breakdown. Aggregates the same
///     <see cref="CachedVisitor"/> rows that feed the rest of the Traffic page
///     into one slice per known family, using the same family→colour mapping
///     as <see cref="HitsPerPeriodChartletBuilder"/> so the donut visually
///     matches the headline stacked bar.
///     <para>
///     Click-to-drill targets the controller's <c>bot_type</c> filter, like
///     the headline stacked bar, swapping <c>#traffic-panels</c> in place via
///     HTMX -- so clicking a Scraper slice re-renders the side panels under
///     a Scraper filter.
///     </para>
/// </summary>
public static class BotFamiliesDonutBuilder
{
    /// <summary>
    ///     Family axis, ordered to match
    ///     <see cref="HitsPerPeriodChartletBuilder"/>. Order matters for legend
    ///     rendering and visual continuity between the two charts. ColorToken
    ///     values follow project_dashboard_color_semantics: verified=success
    ///     (human/aligned), elevated=warning (suspicious / uncertain bot),
    ///     veryhigh=danger (confirmed bot), unknown=neutral (internal).
    /// </summary>
    private static readonly (string Key, string Label, string Token)[] Families =
    {
        ("Human",        "Human",         "--sb-color-risk-verified"),
        ("Suspicious",   "Suspicious",    "--sb-color-risk-elevated"),
        ("SearchEngine", "Search engine", "--sb-color-risk-verylow"),
        ("GoodBot",      "Good bot",      "--sb-color-risk-verylow"),
        ("Scraper",      "Scraper",       "--sb-color-risk-veryhigh"),
        ("Tool",         "Tool",          "--sb-color-risk-high"),
        ("Unknown",      "Unknown bot",   "--sb-color-risk-elevated"),
        ("Internal",     "Internal",      "--sb-color-risk-unknown"),
    };

    /// <summary>
    ///     Build a donut chartlet from the visitor projection. Each family
    ///     emits a single-bucket series; the donut shape draws one slice per
    ///     series. Families with zero hits are still emitted so the legend
    ///     and colour assignment stay stable across refreshes.
    /// </summary>
    public static ChartletViewModel Build(IReadOnlyList<CachedVisitor> rows)
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, _, _) in Families)
        {
            totals[key] = 0L;
        }

        foreach (var v in rows)
        {
            var familyKey = ResolveFamilyKey(v);
            totals[familyKey] += v.Hits;
        }

        // Donut has a single conceptual bucket per series ("all visitors in
        // window"); BucketLabels stays minimal so the tooltip footer total
        // still reads correctly on hover.
        var labels = new[] { "share" };
        var series = Families
            .Select(f => new ChartletSeries(
                Key: f.Key,
                Label: f.Label,
                ColorToken: f.Token,
                Buckets: new[] { totals[f.Key] }))
            .ToList();

        return new ChartletViewModel(
            Id: "traffic-bot-families",
            Kind: ChartletKind.Donut,
            BucketLabels: labels,
            Series: series,
            // Donuts don't render axes but the record requires the value;
            // YFormat=number keeps tooltip footer values legible.
            Axes: new ChartletAxes(YLabel: string.Empty, YFormat: "number", XLabel: string.Empty, GridLines: false),
            Drill: new ChartletDrill(
                Url: "/dashboard/traffic",
                ParamKey: "bot_type",
                PanelTarget: "#traffic-panels"));
    }

    /// <summary>
    ///     Mirrors <see cref="HitsPerPeriodChartletBuilder.ResolveFamilyKey"/>
    ///     so both charts agree on the family for any given visitor. Kept
    ///     local because the two builders both ship a stable family table and
    ///     keeping the lookup adjacent to that table is clearer than sharing
    ///     a helper across two narrow callers.
    /// </summary>
    private static string ResolveFamilyKey(CachedVisitor v)
    {
        var isBot = v.IsBot || v.BotProbability >= 0.8;
        if (!isBot)
        {
            return v.BotProbability >= 0.3 ? "Suspicious" : "Human";
        }

        if (string.IsNullOrWhiteSpace(v.BotType))
        {
            return "Unknown";
        }

        foreach (var (key, _, _) in Families)
        {
            if (string.Equals(key, v.BotType, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return "Unknown";
    }
}