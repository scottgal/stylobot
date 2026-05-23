namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     Host -> profile id mapping. Bound from
///     <c>BotDetection:Sites</c>. The resolver compiles this into a frozen
///     exact-match dictionary plus a wildcard list at startup.
/// </summary>
/// <remarks>
///     Wildcard rules use a leading <c>*.</c> for subdomain matching
///     (<c>*.staging.example.com</c> matches <c>foo.staging.example.com</c>
///     but NOT <c>staging.example.com</c>). A <c>*.</c> in the middle
///     matches a single segment (<c>api.*.example.com</c> matches
///     <c>api.eu.example.com</c>). First-match wins; rules are evaluated
///     in declaration order.
/// </remarks>
public sealed class SiteMapOptions
{
    public const string SectionName = "BotDetection:Sites";

    /// <summary>Profile id used when no host rule matches. Default: <c>"generic"</c>.</summary>
    public string DefaultProfile { get; set; } = "generic";

    /// <summary>Host -> profile rules. First-match wins.</summary>
    public List<SiteMapRule> Domains { get; set; } = new();
}

public sealed class SiteMapRule
{
    /// <summary>
    ///     Hostname pattern. Examples:
    ///     <list type="bullet">
    ///         <item><c>blog.example.com</c> -- exact match</item>
    ///         <item><c>*.staging.example.com</c> -- any subdomain</item>
    ///         <item><c>api.*.example.com</c> -- any single mid segment</item>
    ///     </list>
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>Profile id to apply when this rule matches.</summary>
    public string Profile { get; set; } = "";
}
