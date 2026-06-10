namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     URL-hierarchy slot on a <see cref="PolicyScope"/>. Variants are
///     ordered most-specific-first by the resolver: <see cref="Endpoint"/>
///     beats <see cref="Subdomain"/> beats <see cref="Domain"/>, and a
///     null Host slot is the wildcard ("applies to every host").
///
///     <para>
///     The discriminated union mirrors the legacy <c>PolicyScope.Domain /
///     Subdomain / Endpoint</c> shapes byte-for-byte so the YAML on-disk
///     format, the Postgres column projection, and the URL-walk semantics
///     all carry forward unchanged.
///     </para>
/// </summary>
public abstract record HostScope
{
    /// <summary>Matches every request whose host sits under <paramref name="Name"/>.</summary>
    public sealed record Domain(string Name) : HostScope;

    /// <summary>
    ///     Matches every request whose host equals <paramref name="SubdomainName"/>
    ///     under <paramref name="DomainName"/>.
    /// </summary>
    public sealed record Subdomain(string DomainName, string SubdomainName) : HostScope;

    /// <summary>
    ///     Matches a specific path template (e.g. <c>"GET /api/upload"</c>) on
    ///     a given subdomain. <see cref="PathTemplate"/> is opaque to the
    ///     <see cref="HostScope"/> type itself.
    /// </summary>
    public sealed record Endpoint(string DomainName, string SubdomainName, string PathTemplate) : HostScope;
}
