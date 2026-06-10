namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Predicate-style matcher that answers: does this <see cref="PolicyScope"/>
///     apply to a request described by the given signals dictionary?
///
///     <para>
///         A scope matches when EVERY non-null slot matches the request. The
///         four slots are independent:
///         <list type="bullet">
///           <item><description><see cref="PolicyScope.Host"/> -- matched via <see cref="HostMatches"/> against <c>request.domain</c> / <c>request.subdomain</c> / <c>request.path</c>.</description></item>
///           <item><description><see cref="PolicyScope.Method"/> -- case-insensitive equality against <c>request.method</c>.</description></item>
///           <item><description><see cref="PolicyScope.Geo"/> -- case-insensitive equality against <c>geo.country</c> (alpha-2 ISO code).</description></item>
///           <item><description><see cref="PolicyScope.Identity"/> -- matched via <see cref="IdentityMatches"/> against the canonical identity.* signals.</description></item>
///         </list>
///     </para>
///
///     <para>
///         The matcher does NOT evaluate the rule's predicate -- it only
///         answers "should this rule even be considered for this request". The
///         resolver runs both stages in sequence (scope match, then predicate
///         match).
///     </para>
/// </summary>
public static class PolicyScopeMatcher
{
    /// <summary>
    ///     Signal key for the request's HTTP method (e.g. <c>"GET"</c>).
    ///     Matches the canonical <c>request.method</c> mapping already used
    ///     by SignatureEscalatorAtom and the escalator config.
    /// </summary>
    public const string RequestMethodKey = "request.method";

    /// <summary>Signal key for the request's apex domain (e.g. <c>"acme.com"</c>).</summary>
    public const string RequestDomainKey = "request.domain";

    /// <summary>Signal key for the request's full host / subdomain (e.g. <c>"docs.acme.com"</c>).</summary>
    public const string RequestSubdomainKey = "request.subdomain";

    /// <summary>Signal key for the request path (e.g. <c>"/api/upload"</c>).</summary>
    public const string RequestPathKey = "request.path";

    /// <summary>Signal key for the ISO-3166 alpha-2 country code (e.g. <c>"US"</c>).</summary>
    public const string GeoCountryKey = "geo.country";

    /// <summary>Signal key for a named-bot family classification (e.g. <c>"googlebot"</c>).</summary>
    public const string IdentityNamedBotKey = "identity.named_bot";

    /// <summary>Signal key for a bot category (e.g. <c>"scraper"</c>, <c>"headless"</c>).</summary>
    public const string IdentityBotTypeKey = "identity.bot_type";

    /// <summary>Signal key for a human browser family (e.g. <c>"chrome"</c>).</summary>
    public const string IdentityHumanBrowserKey = "identity.human_browser";

    /// <summary>Signal key for the fingerprint id (UUID string).</summary>
    public const string IdentityFingerprintIdKey = "identity.fingerprint_id";

    /// <summary>
    ///     Answer whether <paramref name="scope"/> matches the request
    ///     described by <paramref name="signals"/>. All non-null slots must
    ///     match; a null slot is wildcard for that axis.
    /// </summary>
    public static bool MatchesRequest(
        PolicyScope scope,
        IReadOnlyDictionary<string, object?> signals)
    {
        if (scope.Host is not null && !HostMatches(scope.Host, signals)) return false;

        if (scope.Method is not null
            && !EqualsIgnoreCase(scope.Method, GetString(signals, RequestMethodKey)))
            return false;

        if (scope.Geo is not null
            && !EqualsIgnoreCase(scope.Geo, GetString(signals, GeoCountryKey)))
            return false;

        if (scope.Identity is not null && !IdentityMatches(scope.Identity, signals)) return false;

        return true;
    }

    /// <summary>
    ///     Host slot semantics. Domain scope matches when the request domain is
    ///     the same; Subdomain scope additionally requires the subdomain match;
    ///     Endpoint scope additionally requires the path template match (exact
    ///     equality today; richer template logic lives in the request pipeline).
    /// </summary>
    public static bool HostMatches(
        HostScope host,
        IReadOnlyDictionary<string, object?> signals)
    {
        return host switch
        {
            HostScope.Domain d =>
                EqualsIgnoreCase(d.Name, GetString(signals, RequestDomainKey)),

            HostScope.Subdomain s =>
                EqualsIgnoreCase(s.DomainName, GetString(signals, RequestDomainKey))
                && EqualsIgnoreCase(s.SubdomainName, GetString(signals, RequestSubdomainKey)),

            HostScope.Endpoint e =>
                EqualsIgnoreCase(e.DomainName, GetString(signals, RequestDomainKey))
                && EqualsIgnoreCase(e.SubdomainName, GetString(signals, RequestSubdomainKey))
                && EqualsIgnoreCase(e.PathTemplate, GetString(signals, RequestPathKey)),

            _ => false
        };
    }

    /// <summary>
    ///     Identity slot semantics. Each variant matches its canonical signal
    ///     key by case-insensitive equality. An absent signal never matches a
    ///     non-null Identity scope (the rule simply doesn't fire).
    /// </summary>
    public static bool IdentityMatches(
        IdentityScope identity,
        IReadOnlyDictionary<string, object?> signals)
    {
        return identity switch
        {
            IdentityScope.NamedBot n =>
                EqualsIgnoreCase(n.Family, GetString(signals, IdentityNamedBotKey)),

            IdentityScope.BotType b =>
                EqualsIgnoreCase(b.Category, GetString(signals, IdentityBotTypeKey)),

            IdentityScope.HumanBrowser h =>
                EqualsIgnoreCase(h.Family, GetString(signals, IdentityHumanBrowserKey)),

            IdentityScope.Fingerprint f =>
                EqualsIgnoreCase(f.Id, GetString(signals, IdentityFingerprintIdKey)),

            _ => false
        };
    }

    private static bool EqualsIgnoreCase(string a, string? b) =>
        b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(IReadOnlyDictionary<string, object?> signals, string key) =>
        signals.TryGetValue(key, out var value) ? value as string : null;
}
