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

    /// <summary>Composite scope (host / method / geo / identity slots). Accepts the legacy single-kind shape too.</summary>
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

    /// <summary>
    ///     Optional oscilloscope-trigger metadata. When present, the rule is
    ///     evaluated by the background MeterTriggerService against meter
    ///     snapshots and walks an armed-state machine. See
    ///     <see cref="YamlRuleTrigger"/> and <see cref="RuleTriggerOptions"/>.
    /// </summary>
    [YamlMember("trigger")] public YamlRuleTrigger? Trigger { get; init; }
}

/// <summary>
///     YAML representation of <see cref="PolicyScope"/>. Two shapes are
///     accepted by <see cref="YamlPolicyRuleStore"/>:
///
///     <list type="bullet">
///       <item><description>
///         <b>Legacy single-kind shape</b> -- a top-level <c>kind</c> field
///         drives a URL-only scope. The <c>domain</c>/<c>subdomain</c>/
///         <c>path_template</c> fields are read alongside.
///       </description></item>
///       <item><description>
///         <b>Composite shape</b> -- top-level <c>kind</c> is empty; the
///         optional <c>host</c>, <c>method</c>, <c>geo</c>, and <c>identity</c>
///         slots are populated as needed.
///       </description></item>
///     </list>
///
///     The legacy fields stay on the type for back-compat: an existing
///     seed YAML with <c>kind: domain</c> + <c>domain: acme.com</c> parses
///     without re-write.
/// </summary>
[YamlObject]
public sealed partial class YamlRuleScope
{
    /// <summary>
    ///     LEGACY shape only. One of <c>wildcard</c>, <c>domain</c>,
    ///     <c>subdomain</c>, <c>endpoint</c>. When non-empty, the composite
    ///     slots are ignored. Case-insensitive.
    /// </summary>
    [YamlMember("kind")] public string? Kind { get; init; }

    /// <summary>LEGACY shape only. Apex domain name. Required for legacy domain/subdomain/endpoint scopes.</summary>
    [YamlMember("domain")] public string? Domain { get; init; }

    /// <summary>LEGACY shape only. Subdomain host. Required for legacy subdomain/endpoint scopes.</summary>
    [YamlMember("subdomain")] public string? Subdomain { get; init; }

    /// <summary>LEGACY shape only. Path template (e.g. <c>"GET /api/upload"</c>). Required for legacy endpoint scope.</summary>
    [YamlMember("path_template")] public string? PathTemplate { get; init; }

    // ---- Composite shape slots ----

    /// <summary>Composite host slot. See <see cref="YamlRuleHost"/>. Optional.</summary>
    [YamlMember("host")] public YamlRuleHost? Host { get; init; }

    /// <summary>Composite HTTP-method slot (uppercase). Optional.</summary>
    [YamlMember("method")] public string? Method { get; init; }

    /// <summary>Composite geo slot (ISO-3166 alpha-2 country code). Optional.</summary>
    [YamlMember("geo")] public string? Geo { get; init; }

    /// <summary>Composite identity slot. See <see cref="YamlRuleIdentity"/>. Optional.</summary>
    [YamlMember("identity")] public YamlRuleIdentity? Identity { get; init; }
}

/// <summary>YAML representation of the <see cref="HostScope"/> slot on a composite-shape <see cref="PolicyScope"/>.</summary>
[YamlObject]
public sealed partial class YamlRuleHost
{
    /// <summary>One of <c>domain</c>, <c>subdomain</c>, <c>endpoint</c>. Case-insensitive.</summary>
    [YamlMember("kind")] public string? Kind { get; init; }

    /// <summary>Apex domain name (e.g. <c>acme.com</c>). For <c>domain</c> scope this is the apex.</summary>
    [YamlMember("name")] public string? Name { get; init; }

    /// <summary>Apex domain name (for <c>subdomain</c>/<c>endpoint</c> scopes).</summary>
    [YamlMember("domain")] public string? Domain { get; init; }

    /// <summary>Subdomain host (for <c>subdomain</c>/<c>endpoint</c> scopes).</summary>
    [YamlMember("subdomain")] public string? Subdomain { get; init; }

    /// <summary>Path template (for <c>endpoint</c> scope).</summary>
    [YamlMember("path_template")] public string? PathTemplate { get; init; }
}

/// <summary>YAML representation of the <see cref="IdentityScope"/> slot on a composite-shape <see cref="PolicyScope"/>.</summary>
[YamlObject]
public sealed partial class YamlRuleIdentity
{
    /// <summary>One of <c>named_bot</c>, <c>bot_type</c>, <c>human_browser</c>, <c>fingerprint</c>. Case-insensitive.</summary>
    [YamlMember("kind")] public string? Kind { get; init; }

    /// <summary>Named-bot family OR human-browser family.</summary>
    [YamlMember("family")] public string? Family { get; init; }

    /// <summary>Bot category (e.g. <c>scraper</c>).</summary>
    [YamlMember("category")] public string? Category { get; init; }

    /// <summary>Fingerprint id (UUID string).</summary>
    [YamlMember("id")] public string? Id { get; init; }
}

/// <summary>
///     YAML representation of <see cref="PolicyAction"/>. <see cref="Kind"/>
///     selects the subtype; the remaining fields are read only when relevant.
/// </summary>
[YamlObject]
public sealed partial class YamlRuleAction
{
    /// <summary>One of <c>allow</c>, <c>observe</c>, <c>tag</c>, <c>challenge</c>, <c>rate_limit</c>, <c>throttle</c>, <c>block</c>, <c>redirect</c>, <c>route_swap</c>. Case-insensitive.</summary>
    [YamlMember("kind")] public string Kind { get; init; } = "observe";

    /// <summary>Absolute http(s) URL. Read only for <c>redirect</c> and <c>route_swap</c>.</summary>
    [YamlMember("target")] public string? Target { get; init; }

    /// <summary>Challenge implementation (e.g. <c>turnstile</c>, <c>captcha</c>). Read only for <c>challenge</c>.</summary>
    [YamlMember("challenge_kind")] public string? ChallengeKind { get; init; }

    /// <summary>Tag label. Read only for <c>tag</c>.</summary>
    [YamlMember("tag_name")] public string? TagName { get; init; }

    /// <summary>Rate-limit budget. Read only for <c>rate_limit</c>.</summary>
    [YamlMember("requests_per_minute")] public int? RequestsPerMinute { get; init; }

    /// <summary>Throttle steady-state cap in requests per second. Read only for <c>throttle</c>.</summary>
    [YamlMember("rps")] public int? RequestsPerSecond { get; init; }

    /// <summary>Optional operator-facing label for throttle decisions. Read only for <c>throttle</c>.</summary>
    [YamlMember("reason")] public string? Reason { get; init; }

    /// <summary>Optional HTTP status for a <c>block</c> action. Null keeps the default 403; 404 deflects a scan.</summary>
    [YamlMember("status")] public int? Status { get; init; }
}

/// <summary>
///     YAML representation of <see cref="RuleTriggerOptions"/>. <see cref="SustainFor"/>
///     and <see cref="RecoverAfter"/> accept the small human-duration grammar
///     parsed by <see cref="DurationParser"/> (<c>30s</c>, <c>5m</c>, <c>1.5h</c>,
///     <c>1d</c>) as well as the canonical <c>hh:mm:ss</c> shape.
/// </summary>
[YamlObject]
public sealed partial class YamlRuleTrigger
{
    /// <summary>
    ///     Sustain window: how long the predicate must stay continuously true
    ///     before the rule arms. Missing / empty / unparseable values fall
    ///     through to the <see cref="RuleTriggerOptions.DefaultSustainFor"/>
    ///     spec floor.
    /// </summary>
    [YamlMember("sustain_for")] public string? SustainFor { get; init; }

    /// <summary>
    ///     Recovery window: how long the predicate must stay continuously
    ///     false before the rule unarms. Missing / empty / unparseable values
    ///     fall through to the <see cref="RuleTriggerOptions.DefaultRecoverAfter"/>
    ///     spec floor. Should be &gt;= sustain to avoid flap.
    /// </summary>
    [YamlMember("recover_after")] public string? RecoverAfter { get; init; }

    /// <summary>
    ///     Optional override action engaged while the rule is ARMED. Same
    ///     nested-action shape as the top-level <c>action</c> field.
    /// </summary>
    [YamlMember("action_while_armed")] public YamlRuleAction? ActionWhileArmed { get; init; }
}
