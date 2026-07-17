namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Options controlling the Threats card on <c>/dashboard/traffic</c>.
///     The card was historically filtered to <c>ThreatBand</c> Medium / High /
///     Critical only, which after the parasitic header-source rip (#178) left
///     real bot rows (Scraper / Tool / Unknown) invisible — they score
///     <c>ThreatBand = None</c> with a high <c>BotProbability</c>. The widened
///     filter (Task A4 of the cohesive-charts plan) adds the probability floor
///     as an OR clause so high-probability bots surface even when no band has
///     been assigned. Configurable per
///     <c>feedback_all_settings_configurable</c>.
/// </summary>
public sealed class ThreatsOptions
{
    /// <summary>
    ///     Configuration section binding root. Wired via
    ///     <c>services.AddOptions&lt;ThreatsOptions&gt;().BindConfiguration(SectionName)</c>
    ///     in <c>AddStyloBotDashboard</c>.
    /// </summary>
    public const string SectionName = "BotDetection:Ui:Threats";

    /// <summary>
    ///     <see cref="Models.ProjectedVisitor.BotProbability"/> threshold (0..1)
    ///     at and above which a row qualifies for the Threats panel even when
    ///     its <see cref="Models.ProjectedVisitor.ThreatBand"/> is not in the
    ///     severe set. 0.8 catches confidently-classified bot rows while
    ///     keeping ambiguous (0.4..0.7) traffic in the broader visitor table.
    /// </summary>
    public double LowBotProbabilityFloor { get; init; } = 0.8;

    /// <summary>
    ///     Maximum number of rows the panel renders. Mirrors the per-card
    ///     top-N other Traffic cards use, but kept independent so this knob
    ///     can be tuned without changing the breakdown cards.
    /// </summary>
    public int TopN { get; init; } = 8;
}
