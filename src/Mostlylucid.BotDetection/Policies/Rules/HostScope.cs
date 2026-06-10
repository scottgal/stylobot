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

    /// <summary>
    ///     Deterministic, case-folded string projection used as a bucket-key
    ///     fragment (e.g. by the token-bucket throttle handler). Two
    ///     structurally equal <see cref="HostScope"/> instances MUST produce
    ///     identical output; the shape does not round-trip.
    /// </summary>
    public string ToStableKey() => this switch
    {
        Endpoint e => $"endpoint:{(e.DomainName ?? string.Empty).ToLowerInvariant()}|" +
                      $"{(e.SubdomainName ?? string.Empty).ToLowerInvariant()}|" +
                      $"{e.PathTemplate ?? string.Empty}",
        Subdomain s => $"subdomain:{(s.DomainName ?? string.Empty).ToLowerInvariant()}|" +
                       $"{(s.SubdomainName ?? string.Empty).ToLowerInvariant()}",
        Domain d => $"domain:{(d.Name ?? string.Empty).ToLowerInvariant()}",
        _ => "wildcard",
    };
}
