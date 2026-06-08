using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     On-disk YAML representation of one <see cref="PolicyRule"/>. The file
///     format stays human-friendly (snake_case, predicate as a single string
///     in the policy expression language) and is parsed into the in-memory
///     <see cref="PolicyRule"/> by <see cref="YamlPolicyRuleStore"/>.
/// </summary>
[YamlObject]
public sealed partial class YamlRuleFile
{
    /// <summary>Stable Guid identifier for the rule, written as the standard 8-4-4-4-12 hex form.</summary>
    [YamlMember("id")] public string Id { get; init; } = "";

    /// <summary>Where the rule attaches in the wildcard / domain / subdomain / endpoint hierarchy.</summary>
    [YamlMember("scope")] public YamlRuleScope Scope { get; init; } = new();

    /// <summary>Lower = higher priority. Within a scope, ordered ascending.</summary>
    [YamlMember("priority")] public int Priority { get; init; }

    /// <summary>Predicate in the expression language understood by <c>PredicateParser.Parse</c>.</summary>
    [YamlMember("predicate")] public string Predicate { get; init; } = "";

    /// <summary>Action to apply when the predicate evaluates true.</summary>
    [YamlMember("action")] public YamlRuleAction Action { get; init; } = new();

    /// <summary>Lifecycle posture; case-insensitive mapping of <see cref="PolicyMode"/>. Defaults to <c>live</c>.</summary>
    [YamlMember("mode")] public string Mode { get; init; } = "live";

    /// <summary>Human notes; never user-facing on the request path.</summary>
    [YamlMember("notes")] public string Notes { get; init; } = "";
}

/// <summary>
///     YAML representation of <see cref="PolicyScope"/>. <see cref="Kind"/>
///     drives which other fields are read; unused fields are ignored at parse.
/// </summary>
[YamlObject]
public sealed partial class YamlRuleScope
{
    /// <summary>One of <c>wildcard</c>, <c>domain</c>, <c>subdomain</c>, <c>endpoint</c>. Case-insensitive.</summary>
    [YamlMember("kind")] public string Kind { get; init; } = "wildcard";

    /// <summary>Apex domain name (e.g. <c>acme.com</c>). Required for domain/subdomain/endpoint scopes.</summary>
    [YamlMember("domain")] public string? Domain { get; init; }

    /// <summary>Subdomain host (e.g. <c>docs.acme.com</c>). Required for subdomain/endpoint scopes.</summary>
    [YamlMember("subdomain")] public string? Subdomain { get; init; }

    /// <summary>Path template (e.g. <c>"GET /api/upload"</c>). Required for endpoint scope.</summary>
    [YamlMember("path_template")] public string? PathTemplate { get; init; }
}

/// <summary>
///     YAML representation of <see cref="PolicyAction"/>. <see cref="Kind"/>
///     selects the subtype; the remaining fields are read only when relevant.
/// </summary>
[YamlObject]
public sealed partial class YamlRuleAction
{
    /// <summary>One of <c>allow</c>, <c>observe</c>, <c>tag</c>, <c>challenge</c>, <c>rate_limit</c>, <c>block</c>. Case-insensitive.</summary>
    [YamlMember("kind")] public string Kind { get; init; } = "observe";

    /// <summary>Challenge implementation (e.g. <c>turnstile</c>, <c>captcha</c>). Read only for <c>challenge</c>.</summary>
    [YamlMember("challenge_kind")] public string? ChallengeKind { get; init; }

    /// <summary>Tag label. Read only for <c>tag</c>.</summary>
    [YamlMember("tag_name")] public string? TagName { get; init; }

    /// <summary>Rate-limit budget. Read only for <c>rate_limit</c>.</summary>
    [YamlMember("requests_per_minute")] public int? RequestsPerMinute { get; init; }
}
