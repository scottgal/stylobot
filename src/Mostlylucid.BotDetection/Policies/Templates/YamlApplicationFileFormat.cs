using VYaml.Annotations;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     On-disk YAML shape of one <see cref="TemplateApplication"/>. The file
///     format is human-friendly (snake-case + dash-case keys, applied-to
///     scopes flattened to per-entry fields). Parsed into a
///     <see cref="TemplateApplication"/> by <see cref="YamlTemplateStore"/>.
///
///     <para>
///         Example:
///         <code>
///         application: my-login-protection
///         template: protect-login
///         applied-to:
///           - { host: { kind: subdomain, domain: example.com, sub: api }, method: POST, host-path: /login }
///         parameters:
///           human-rpm: 10
///           credstuff-threshold: 0.7
///         </code>
///     </para>
/// </summary>
[YamlObject]
public sealed partial class YamlApplicationFile
{
    /// <summary>Customer-scoped application id (e.g. <c>"my-login-protection"</c>).</summary>
    [YamlMember("application")] public string Application { get; init; } = "";

    /// <summary>Reference to the <see cref="Template.Id"/> this application instantiates.</summary>
    [YamlMember("template")] public string Template { get; init; } = "";

    /// <summary>
    ///     Composite scopes the template's expansion attaches to. Empty / missing
    ///     means "global wildcard" -- the resolver expands a single wildcard
    ///     scope when this list is empty.
    /// </summary>
    [YamlMember("applied-to")] public List<YamlApplicationScope>? AppliedTo { get; init; }

    /// <summary>
    ///     Application parameter values keyed by parameter name. Values are
    ///     parsed as the YAML scalar type (int, decimal, string, bool); the
    ///     resolver formats them with invariant culture into the predicate /
    ///     action text. Lookups are case-insensitive.
    /// </summary>
    [YamlMember("parameters")] public Dictionary<string, object?>? Parameters { get; init; }
}

/// <summary>
///     YAML representation of one composite scope inside an application's
///     <c>applied-to</c> list. Mirrors the fields a <see cref="PolicyScope"/>
///     carries -- a nested <see cref="YamlApplicationHost"/>, and the
///     orthogonal <c>method</c> + <c>geo</c> slots.
/// </summary>
[YamlObject]
public sealed partial class YamlApplicationScope
{
    /// <summary>Optional host slot. Null = matches any host.</summary>
    [YamlMember("host")] public YamlApplicationHost? Host { get; init; }

    /// <summary>Optional HTTP-method slot (case-insensitive). Null = any verb.</summary>
    [YamlMember("method")] public string? Method { get; init; }

    /// <summary>Optional ISO-3166 alpha-2 country code. Null = any country.</summary>
    [YamlMember("geo")] public string? Geo { get; init; }

    /// <summary>
    ///     Optional path template carried as a sibling field for endpoint-shaped
    ///     scopes. When present together with a subdomain host, the loader
    ///     promotes the host kind to <c>endpoint</c> automatically. Matches the
    ///     example shape in the spec: <c>{ host: { kind: subdomain, ...}, host-path: /login }</c>.
    /// </summary>
    [YamlMember("host-path")] public string? HostPath { get; init; }
}

/// <summary>
///     YAML representation of the host slot inside an application's
///     <c>applied-to</c> entry. Recognises <c>domain</c>, <c>subdomain</c>,
///     and <c>endpoint</c> via the <see cref="Kind"/> field; field names
///     follow the spec example (<c>domain</c>, <c>sub</c>).
/// </summary>
[YamlObject]
public sealed partial class YamlApplicationHost
{
    /// <summary>One of <c>domain</c>, <c>subdomain</c>, <c>endpoint</c>. Case-insensitive.</summary>
    [YamlMember("kind")] public string? Kind { get; init; }

    /// <summary>Apex domain name (e.g. <c>example.com</c>).</summary>
    [YamlMember("domain")] public string? Domain { get; init; }

    /// <summary>Subdomain host (e.g. <c>api</c>). Spec example uses the short <c>sub</c> name.</summary>
    [YamlMember("sub")] public string? Sub { get; init; }

    /// <summary>Path template (for <c>endpoint</c> scope).</summary>
    [YamlMember("path_template")] public string? PathTemplate { get; init; }
}
