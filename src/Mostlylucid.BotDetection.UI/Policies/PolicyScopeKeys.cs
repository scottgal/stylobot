using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Policies;

/// <summary>
///     Pure helpers for the SignalR scope-group routing the B6 live-update
///     beacon relies on. Three things live here:
///
///     <list type="bullet">
///       <item><description>
///         <see cref="ScopeHash"/> -- stable 16-hex prefix of the canonical
///         scope key. Used as the SignalR group name on both ends of the wire.
///       </description></item>
///       <item><description>
///         <see cref="ScopeKey"/> -- canonical "kind|domain|sub|template"
///         string. The broadcaster ships this on the wire so the browser can
///         decide which row sections to refresh without re-hashing.
///       </description></item>
///       <item><description>
///         <see cref="WalkAncestors"/> + <see cref="AncestorHashes"/> --
///         decompose any scope into its Wildcard/Domain/Subdomain/Endpoint
///         ancestor chain so a browser viewing at Endpoint scope joins every
///         enclosing group too. That way a Domain-level mutation reaches every
///         dashboard sitting on a child Subdomain/Endpoint.
///       </description></item>
///     </list>
///
///     <para>
///     Kept dependency-free so the broadcaster, the view component, the
///     middleware, and the (FOSS or commercial) ChangeNotification host all
///     share ONE source of truth on what group name encodes what scope.
///     </para>
/// </summary>
public static class PolicyScopeKeys
{
    /// <summary>
    ///     Canonical scope key. Mirrors <c>PolicyStackPresenter.ComputeScopeHash</c>'s
    ///     pre-image so the hash matches whether you compute it from the
    ///     presenter or from here.
    /// </summary>
    public static string ScopeKey(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "wildcard||||",
        PolicyScope.Domain d => $"domain|{d.DomainName}|||",
        PolicyScope.Subdomain s => $"subdomain|{s.DomainName}|{s.SubdomainName}||",
        PolicyScope.Endpoint e => $"endpoint|{e.DomainName}|{e.SubdomainName}|{e.PathTemplate}|",
        _ => "unknown||||"
    };

    /// <summary>
    ///     SHA-256(<see cref="ScopeKey"/>), truncated to the first 16 hex chars
    ///     (8 bytes / 64 bits). Collision risk on a per-process group dictionary
    ///     is negligible at this width.
    /// </summary>
    public static string ScopeHash(PolicyScope scope)
    {
        var bytes = Encoding.UTF8.GetBytes(ScopeKey(scope));
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    ///     Walk a scope into the ancestor chain that owns the live-update
    ///     fan-out. The list is ordered from broadest (Wildcard) to most
    ///     specific (the scope itself) so callers can render breadcrumbs and
    ///     join groups in a single pass.
    /// </summary>
    public static IReadOnlyList<PolicyScope> WalkAncestors(PolicyScope scope)
    {
        // Wildcard sits above every other scope by definition. We always
        // include it so a wildcard-level rule mutation reaches every browser
        // regardless of how deeply it was scoped at render time.
        var ancestors = new List<PolicyScope> { new PolicyScope.Wildcard() };
        switch (scope)
        {
            case PolicyScope.Wildcard:
                break;
            case PolicyScope.Domain d:
                ancestors.Add(d);
                break;
            case PolicyScope.Subdomain s:
                ancestors.Add(new PolicyScope.Domain(s.DomainName));
                ancestors.Add(s);
                break;
            case PolicyScope.Endpoint e:
                ancestors.Add(new PolicyScope.Domain(e.DomainName));
                ancestors.Add(new PolicyScope.Subdomain(e.DomainName, e.SubdomainName));
                ancestors.Add(e);
                break;
        }
        return ancestors;
    }

    /// <summary>
    ///     <see cref="ScopeHash"/>-mapped <see cref="WalkAncestors"/>. The
    ///     Razor partials emit this as a comma-separated <c>data-*</c>
    ///     attribute the client-side glue parses to decide which SignalR
    ///     groups to join.
    /// </summary>
    public static IReadOnlyList<string> AncestorHashes(PolicyScope scope)
    {
        var ancestors = WalkAncestors(scope);
        var result = new string[ancestors.Count];
        for (var i = 0; i < ancestors.Count; i++)
            result[i] = ScopeHash(ancestors[i]);
        return result;
    }
}
