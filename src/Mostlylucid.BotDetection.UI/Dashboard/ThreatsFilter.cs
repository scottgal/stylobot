using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Pure projection that decides which visitor rows belong on the
///     <c>/dashboard/traffic</c> Threats card. Lives next to
///     <see cref="DashboardRoutingHelpers"/> so the partial can stay a
///     near-empty view-model wrapper. Replaces the inline
///     <c>visitors.Where(v =&gt; v.ThreatBand is "Medium" or "High" or
///     "Critical")</c> LINQ in <c>_Traffic.cshtml</c>, which after #178 only
///     ever matched Internal pings and hid all real bot pressure (Task A4
///     of the dashboard cohesive-charts plan).
/// </summary>
public static class ThreatsFilter
{
    /// <summary>
    ///     Selects, orders, and caps the visitor rows that surface on the
    ///     Threats panel. Inclusion rule: row is included when either
    ///     <see cref="ProjectedVisitor.ThreatBand"/> is in the severe set
    ///     (Medium / High / Critical) OR <see cref="ProjectedVisitor.BotProbability"/>
    ///     meets the configured floor. Ordering: severity band desc, then
    ///     <see cref="ProjectedVisitor.BotProbability"/> desc — pin Critical
    ///     above a high-probability None even when their probabilities tie.
    /// </summary>
    public static IReadOnlyList<ProjectedVisitor> Apply(
        IEnumerable<ProjectedVisitor> rows,
        ThreatsOptions options)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);

        return rows
            .Where(v =>
                v.ThreatBand is "Medium" or "High" or "Critical"
                || v.BotProbability >= options.LowBotProbabilityFloor)
            .OrderByDescending(v => BandRank(v.ThreatBand))
            .ThenByDescending(v => v.BotProbability)
            .Take(options.TopN)
            .ToList();
    }

    private static int BandRank(string? band) => band switch
    {
        "Critical" => 4,
        "High"     => 3,
        "Medium"   => 2,
        _          => 1
    };
}