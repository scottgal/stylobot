namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Configurable knobs for the adaptive <c>/sitemap.xml</c> endpoint
///     wired via <c>endpoints.MapStyloBotSitemap()</c>. Every value has a
///     baked-in default so callers can opt in with one line and tune later.
///
///     Verdict mapping at request time:
///     <list type="bullet">
///         <item>Verified search engine or other verified bot: <see cref="PublicUrls"/>.</item>
///         <item><c>BotProbability &lt; <see cref="HumanProbabilityCeiling"/></c>: <see cref="PublicUrls"/>.</item>
///         <item><c>BotProbability &gt;= <see cref="BotProbabilityThreshold"/></c>: <see cref="HoneypotPath"/> only.</item>
///         <item>Anything in between: <see cref="UncertainUrls"/> (or <see cref="PublicUrls"/> when unset).</item>
///     </list>
/// </summary>
public sealed class StyloBotSitemapOptions
{
    /// <summary>
    ///     URLs served to verified search engines and high-confidence humans.
    ///     Defaults to a single <c>/</c> entry. Consumers populate this with
    ///     every public site path they want crawled.
    /// </summary>
    public IList<string> PublicUrls { get; set; } = new List<string> { "/" };

    /// <summary>
    ///     URLs served when the verdict sits between
    ///     <see cref="HumanProbabilityCeiling"/> and
    ///     <see cref="BotProbabilityThreshold"/>. Typically a subset of
    ///     <see cref="PublicUrls"/> without admin or api surfaces. When empty,
    ///     <see cref="PublicUrls"/> is reused.
    /// </summary>
    public IList<string> UncertainUrls { get; set; } = new List<string>();

    /// <summary>
    ///     Single honeypot path emitted to visitors flagged at or above
    ///     <see cref="BotProbabilityThreshold"/>. Operators wire a tarpit or
    ///     a bait endpoint here.
    /// </summary>
    public string HoneypotPath { get; set; } = "/honeypot/admin";

    /// <summary>
    ///     <c>BotProbability</c> at or above which the visitor is treated as
    ///     a high-probability bot and served the honeypot list only. Defaults
    ///     to <c>0.7</c> to match the <c>BotDetection:BotThreshold</c> default.
    /// </summary>
    public double BotProbabilityThreshold { get; set; } = 0.7d;

    /// <summary>
    ///     <c>BotProbability</c> below which the visitor is treated as
    ///     human and served <see cref="PublicUrls"/>. Defaults to <c>0.4</c>
    ///     so the mid range falls through to the uncertain branch.
    /// </summary>
    public double HumanProbabilityCeiling { get; set; } = 0.4d;

    /// <summary>
    ///     Whether to prepend an XML comment naming the verdict, risk band,
    ///     probability, and confidence. Useful as a conference demo punchline
    ///     and as an audit trace for SEO consumers. Defaults to <c>true</c>.
    /// </summary>
    public bool EmitVerdictComment { get; set; } = true;
}