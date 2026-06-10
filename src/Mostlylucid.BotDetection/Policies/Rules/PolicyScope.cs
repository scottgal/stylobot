namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     The set of orthogonal constraints a <see cref="PolicyRule"/> attaches to.
///     A rule applies to a request when EVERY non-null slot matches the request.
///     All slots null = wildcard (applies to every request).
///
///     <para>
///         Slots are independent ("Host AND Method AND Geo AND Identity"). The
///         legacy URL-only hierarchy (Wildcard / Domain / Subdomain / Endpoint)
///         lives on the <see cref="Host"/> slot via <see cref="HostScope"/>;
///         orthogonal axes (HTTP verb, country, identity vocabulary) get their
///         own slot so a rule like "throttle POST from RU when armed" is a
///         coherent first-class thing.
///     </para>
///
///     <para>
///         <see cref="Specificity"/> is a weighted sum so URL-hierarchy slots
///         still dominate the orthogonal slots: an endpoint rule beats a
///         subdomain rule beats a domain rule beats "POST from RU" when they
///         collide. See the field summary on <see cref="Specificity"/> for
///         the exact weights.
///     </para>
/// </summary>
/// <param name="Host">URL-hierarchy slot. Null = matches any host.</param>
/// <param name="Method">
///     ISO HTTP verb, uppercase (<c>"GET"</c>, <c>"POST"</c>, ...). Null = any
///     verb. Matching is case-insensitive against the <c>request.method</c>
///     per-request signal.
/// </param>
/// <param name="Geo">
///     ISO-3166 alpha-2 country code (<c>"RU"</c>, <c>"US"</c>, <c>"GB"</c>).
///     Null = any country. Matching is case-insensitive against the
///     <c>geo.country</c> per-request signal.
/// </param>
/// <param name="Identity">
///     Entity-vocabulary slot. Null = any identity. See <see cref="IdentityScope"/>
///     for the four variants.
/// </param>
public sealed record PolicyScope(
    HostScope? Host = null,
    string? Method = null,
    string? Geo = null,
    IdentityScope? Identity = null)
{
    // ---- Convenience factories ------------------------------------------------
    //
    // Preserve the old PolicyScope.Wildcard() / Domain(...) / Subdomain(...) /
    // Endpoint(...) call sites by exposing them as static factory methods that
    // return composite-shape instances with only the Host slot populated. The
    // legacy code path therefore continues to construct scopes idiomatically;
    // pattern-matching call sites switch to inspecting the Host slot instead.

    /// <summary>Wildcard scope: matches every request (all slots null).</summary>
    public static PolicyScope Wildcard() => new();

    /// <summary>Convenience factory: scope with only a Domain host slot.</summary>
    public static PolicyScope Domain(string domain) => new(Host: new HostScope.Domain(domain));

    /// <summary>Convenience factory: scope with only a Subdomain host slot.</summary>
    public static PolicyScope Subdomain(string domain, string subdomain) =>
        new(Host: new HostScope.Subdomain(domain, subdomain));

    /// <summary>Convenience factory: scope with only an Endpoint host slot.</summary>
    public static PolicyScope Endpoint(string domain, string subdomain, string pathTemplate) =>
        new(Host: new HostScope.Endpoint(domain, subdomain, pathTemplate));

    // ---- Properties -----------------------------------------------------------

    /// <summary>All slots null -- matches every request.</summary>
    public bool IsWildcard => Host is null && Method is null && Geo is null && Identity is null;

    /// <summary>
    ///     Weighted specificity ranking. Higher = more specific. Tie-break key
    ///     when two rules match the same request. Weights chosen so that:
    ///     <list type="bullet">
    ///       <item><description>any populated slot beats wildcard,</description></item>
    ///       <item><description>URL-hierarchy slots (<see cref="Host"/>) dominate orthogonal slots,</description></item>
    ///       <item><description>within Host, Endpoint > Subdomain > Domain.</description></item>
    ///     </list>
    /// </summary>
    public int Specificity =>
        Host switch
        {
            HostScope.Endpoint => 8,
            HostScope.Subdomain => 4,
            HostScope.Domain => 2,
            _ => 0,
        }
        + (Method is null ? 0 : 1)
        + (Geo is null ? 0 : 1)
        + (Identity is null ? 0 : 1);
}
