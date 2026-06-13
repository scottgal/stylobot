namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Configurable knobs for the adaptive <c>/robots.txt</c> endpoint
///     wired via <c>endpoints.MapStyloBotRobotsTxt()</c>. Robots.txt is
///     a per-deployment contract (not per-visitor), so this options
///     class carries the rules to emit and the auxiliary fields
///     (sitemap URL, host) that round out a healthy robots document.
/// </summary>
public sealed class StyloBotRobotsTxtOptions
{
    /// <summary>
    ///     One <see cref="RobotsRule"/> per <c>User-agent</c> block to emit.
    ///     Defaults to a single permissive <c>*</c> block with no allow or
    ///     disallow entries. Replace with the site's actual policy.
    /// </summary>
    public IList<RobotsRule> Rules { get; set; } = new List<RobotsRule>
    {
        new RobotsRule { UserAgent = "*" }
    };

    /// <summary>
    ///     Absolute URL to the sitemap to advertise in the
    ///     <c>Sitemap:</c> directive at the bottom of the document. When
    ///     null (the default), the endpoint synthesises
    ///     <c>{Request.Scheme}://{Request.Host}/sitemap.xml</c> at request
    ///     time so the link tracks the deployment automatically.
    /// </summary>
    public string? SitemapUrl { get; set; }

    /// <summary>
    ///     Optional <c>Host:</c> directive value. Most modern crawlers
    ///     ignore this; left null by default.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    ///     Free-text header comment lines emitted at the top of the
    ///     document. The endpoint prepends <c>#</c> automatically.
    /// </summary>
    public IList<string> HeaderComments { get; set; } = new List<string>();
}

/// <summary>
///     One <c>User-agent</c> block in a robots.txt document.
/// </summary>
public sealed class RobotsRule
{
    /// <summary>
    ///     Token that follows the <c>User-agent:</c> directive. Use
    ///     <c>*</c> for the catch-all block, or a specific crawler name
    ///     (<c>Googlebot</c>, <c>bingbot</c>, etc.) for a per-bot block.
    /// </summary>
    public string UserAgent { get; set; } = "*";

    /// <summary>Paths emitted as <c>Allow:</c> directives.</summary>
    public IList<string> Allow { get; set; } = new List<string>();

    /// <summary>Paths emitted as <c>Disallow:</c> directives.</summary>
    public IList<string> Disallow { get; set; } = new List<string>();

    /// <summary>
    ///     Optional <c>Crawl-delay:</c> directive value in seconds.
    ///     Honoured by Bing and Yandex; Google ignores it.
    /// </summary>
    public int? CrawlDelaySeconds { get; set; }
}