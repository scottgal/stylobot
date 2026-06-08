using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Pure URL + label helpers for <see cref="PolicyScope"/> breadcrumbs.
///     Kept separate from the presenter so Razor partials can call into it
///     without dragging the presenter's dependency graph along. The exact
///     route paths are placeholders B7-B9 will replace when the SbPolicyStack
///     control is wired into the 9 dashboard call sites; until then the
///     anchors render as relative hash anchors that don't navigate, which is
///     the right behaviour for an empty-route control under development.
/// </summary>
public static class PolicyScopeUrl
{
    /// <summary>
    ///     Build the route path that drives the breadcrumb anchor. Today this
    ///     emits a stable hash-only URL keyed off the scope discriminator and
    ///     its parts; B7-B9 will replace this with the actual dashboard route
    ///     once the call sites land.
    /// </summary>
    public static string For(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "#policies",
        PolicyScope.Domain d => $"#policies/{Slug(d.DomainName)}",
        PolicyScope.Subdomain s => $"#policies/{Slug(s.DomainName)}/{Slug(s.SubdomainName)}",
        PolicyScope.Endpoint e => $"#policies/{Slug(e.DomainName)}/{Slug(e.SubdomainName)}/{Slug(e.PathTemplate)}",
        _ => "#policies"
    };

    /// <summary>
    ///     Render the breadcrumb segment label for <paramref name="scope"/>.
    ///     Wildcard renders as "All sites"; Domain as the apex; Subdomain as
    ///     the host; Endpoint as the path template.
    /// </summary>
    public static string Label(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "All sites",
        PolicyScope.Domain d => d.DomainName,
        PolicyScope.Subdomain s => s.SubdomainName,
        PolicyScope.Endpoint e => e.PathTemplate,
        _ => "All sites"
    };

    private static string Slug(string s)
    {
        // Very conservative slug: anything not alphanumeric / dot / dash becomes
        // a dash. Good enough for routing keys until B7-B9 lands the real route.
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_')
                sb.Append(ch);
            else
                sb.Append('-');
        }
        return sb.ToString();
    }
}
