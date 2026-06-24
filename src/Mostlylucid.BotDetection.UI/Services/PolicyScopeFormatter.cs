using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Webmaster-readable projection of a <see cref="PolicyScope"/>. The
///     A4 policy-stack-card headline uses the <see cref="ToHeadline"/>
///     output to show the operator the actual hostname / path the rule
///     binds to instead of the structural variant name. The orthogonal
///     slots on <see cref="PolicyScope"/> (Method / Geo / Identity) are
///     intentionally NOT folded into the headline -- the edit-expand
///     surface owns those (Phase B / A8).
/// </summary>
public static class PolicyScopeFormatter
{
    /// <summary>
    ///     One-line, operator-readable label for the Host slot of
    ///     <paramref name="scope"/>. Examples:
    ///     <list type="bullet">
    ///         <item><description><c>"*"</c> -- null Host slot (wildcard).</description></item>
    ///         <item><description><c>"example.com"</c> -- <see cref="HostScope.Domain"/>.</description></item>
    ///         <item><description><c>"api.example.com"</c> -- <see cref="HostScope.Subdomain"/>.</description></item>
    ///         <item><description><c>"api.example.com/v1/users/*"</c> -- <see cref="HostScope.Endpoint"/>.</description></item>
    ///     </list>
    ///     The output is a pure projection of the discriminated union; there
    ///     is no curated string table to maintain and no AND/OR composition
    ///     with the orthogonal slots happens here.
    /// </summary>
    public static string ToHeadline(PolicyScope scope)
    {
        return scope.Host switch
        {
            null                  => "*",
            HostScope.Domain    d => d.Name,
            HostScope.Subdomain s => string.IsNullOrEmpty(s.SubdomainName)
                                         ? s.DomainName
                                         : $"{s.SubdomainName}.{s.DomainName}",
            HostScope.Endpoint  e => string.IsNullOrEmpty(e.SubdomainName)
                                         ? $"{e.DomainName}{e.PathTemplate}"
                                         : $"{e.SubdomainName}.{e.DomainName}{e.PathTemplate}",
            _                     => "*",
        };
    }
}
