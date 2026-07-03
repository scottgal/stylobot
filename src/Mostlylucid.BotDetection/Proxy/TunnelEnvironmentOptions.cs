namespace Mostlylucid.BotDetection.Proxy;

/// <summary>
///     Settings for the startup-time tunnel-enrichment inspector. See
///     <see cref="TunnelEnvironmentInspector"/> -- the inspector observes the first
///     <see cref="MinSamples"/> requests, settles a verdict on what TLS / JA3 / proxy
///     signals reach the gateway, and exposes it for the dashboard to render as a
///     banner. There is no continuous polling; on FOSS the verdict re-evaluates only
///     on process restart, when the deployment shape can have changed.
/// </summary>
public sealed class TunnelEnvironmentOptions
{
    /// <summary>
    ///     Master switch. False disables observation and <c>GetSnapshot</c> always
    ///     returns null -- the dashboard banner stays hidden. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Number of requests to observe before settling the snapshot. Larger
    ///     values reduce the chance that a single curl probe with weird headers
    ///     poisons the verdict; smaller values surface the banner sooner after
    ///     restart. Default 10 -- enough to be representative on any real
    ///     deployment, fast enough to settle on a quiet one.
    /// </summary>
    public int MinSamples { get; set; } = 10;

    /// <summary>
    ///     Public docs URL for the "tunnel trade-off" article the dashboard banner
    ///     links to. Configurable so commercial / self-hosted operators can point
    ///     to their own write-up of the recipe.
    /// </summary>
    public string DocsUrl { get; set; } = "https://stylo.bot/articles/tunnel-trade-off";
}
