namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Where in the (wildcard / domain / subdomain / endpoint) hierarchy a
///     <see cref="PolicyRule"/> is attached. Used by the resolver to walk
///     "most specific scope wins" when evaluating an inbound request.
///
///     <para>
///     Concrete shapes:
///     </para>
///     <list type="bullet">
///       <item><description><see cref="Wildcard"/> -- applies to every request.</description></item>
///       <item><description><see cref="Domain"/> -- applies to one apex domain (and everything under it).</description></item>
///       <item><description><see cref="Subdomain"/> -- applies to one host within an apex domain.</description></item>
///       <item><description><see cref="Endpoint"/> -- applies to a specific path template on a subdomain.</description></item>
///     </list>
///
///     <para>
///     <see cref="Specificity"/> exposes the canonical ordering (0..3) the
///     resolver uses; <see cref="Contains"/> answers "does this scope cover
///     the given candidate scope" -- e.g. a <see cref="Domain"/> contains
///     every <see cref="Subdomain"/> and <see cref="Endpoint"/> with the
///     same domain name.
///     </para>
/// </summary>
public abstract record PolicyScope
{
    /// <summary>Matches every request, regardless of host or path.</summary>
    public sealed record Wildcard : PolicyScope;

    /// <summary>Matches every request whose host sits under <paramref name="DomainName"/>.</summary>
    public sealed record Domain(string DomainName) : PolicyScope;

    /// <summary>Matches every request whose host equals <paramref name="SubdomainName"/> under <paramref name="DomainName"/>.</summary>
    public sealed record Subdomain(string DomainName, string SubdomainName) : PolicyScope;

    /// <summary>
    ///     Matches a specific path template (e.g. <c>"GET /api/upload"</c>) on a given
    ///     subdomain. <see cref="PathTemplate"/> is treated as an opaque key by the
    ///     scope type itself -- the resolver decides what matching semantics to apply.
    /// </summary>
    public sealed record Endpoint(string DomainName, string SubdomainName, string PathTemplate) : PolicyScope;

    /// <summary>
    ///     Canonical specificity ranking. Higher is more specific; the resolver
    ///     uses this to order rules so an endpoint rule beats a subdomain rule
    ///     beats a domain rule beats a wildcard rule.
    /// </summary>
    public int Specificity => this switch
    {
        Wildcard => 0,
        Domain => 1,
        Subdomain => 2,
        Endpoint => 3,
        _ => 0
    };

    /// <summary>
    ///     Returns <c>true</c> when this scope logically covers
    ///     <paramref name="candidate"/>. Wildcard covers everything; a Domain
    ///     covers any Subdomain or Endpoint with the same <c>DomainName</c>;
    ///     a Subdomain covers any Endpoint with the same domain + subdomain;
    ///     anything else falls back to exact equality.
    /// </summary>
    public bool Contains(PolicyScope candidate)
    {
        if (Equals(candidate)) return true;

        return (this, candidate) switch
        {
            (Wildcard, _) => true,
            (Domain d, Subdomain s) => string.Equals(d.DomainName, s.DomainName, StringComparison.OrdinalIgnoreCase),
            (Domain d, Endpoint e) => string.Equals(d.DomainName, e.DomainName, StringComparison.OrdinalIgnoreCase),
            (Subdomain s, Endpoint e) =>
                string.Equals(s.DomainName, e.DomainName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.SubdomainName, e.SubdomainName, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
