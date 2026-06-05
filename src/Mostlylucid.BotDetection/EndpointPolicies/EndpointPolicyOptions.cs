namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Operator-declared per-request policy rules. Evaluated before bot
///     detection runs; first matching rule dispatches a named action
///     (block / throttle / challenge / logonly / etc.) via the existing
///     <see cref="Mostlylucid.BotDetection.Actions.IActionPolicyRegistry"/>.
/// </summary>
/// <remarks>
///     <para>
///         This is operator HTTP policy, NOT detection. It runs whether or
///         not a request is bot-shaped. Use cases:
///         <list type="bullet">
///             <item>"Block POST on <c>/xmlrpc.php</c> on every host"</item>
///             <item>"Reject OPTIONS preflight to <c>/admin/*</c>"</item>
///             <item>"Throttle DELETE on <c>/api/v1/*</c> heavily"</item>
///             <item>"Reject HTTP/1.1 access to <c>/admin/*</c> (h2+ required)"</item>
///             <item>"Block any gRPC on the public-edge gateway"</item>
///         </list>
///     </para>
///     <para>
///         Rules are evaluated top-to-bottom; first match wins. All match
///         fields are optional -- omitting any means "match any value for
///         this field".
///     </para>
/// </remarks>
public sealed class EndpointPolicyOptions
{
    public const string SectionName = "BotDetection:EndpointPolicies";

    /// <summary>Master switch. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rules in declaration order. First-match wins.</summary>
    public List<EndpointPolicyRule> Rules { get; set; } = new();
}

/// <summary>
///     A single rule: a set of optional matchers plus an action to dispatch
///     when every specified matcher matches. Omitted matchers wildcard.
/// </summary>
public sealed class EndpointPolicyRule
{
    /// <summary>
    ///     Host glob. Examples: <c>"blog.example.com"</c> exact match;
    ///     <c>"*.example.com"</c> any subdomain; <c>"api.*.example.com"</c>
    ///     single mid-segment; null/empty matches any host.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>HTTP method. <c>"ANY"</c> or null matches any.</summary>
    public string? Method { get; set; }

    /// <summary>
    ///     Path glob: trailing <c>*</c> for prefix match, otherwise exact
    ///     plus path-segment-boundary match (so <c>/admin</c> matches
    ///     <c>/admin/foo</c> but not <c>/administrator</c>). Null matches any.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    ///     Transport class produced by <see cref="TransportClassifier"/>:
    ///     <c>document / api / grpc / grpc-web / websocket / sse / static</c>.
    ///     <c>"ANY"</c> or null matches any.
    /// </summary>
    public string? Transport { get; set; }

    /// <summary>
    ///     HTTP protocol version. Examples: <c>"1.0"</c>, <c>"1.1"</c>,
    ///     <c>"2"</c>, <c>"3"</c>. Null matches any. When the gateway sits
    ///     behind a proxy that injects <c>X-Client-HTTP-Version</c>, that
    ///     value is preferred over <see cref="Microsoft.AspNetCore.Http.HttpRequest.Protocol"/>.
    /// </summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>
    ///     Browser-mode allowlist. When set, the rule matches only when the
    ///     request's classified browser mode (composite-spec step 5: same
    ///     browser, different modes — <c>navigation / xhr / sub-resource /
    ///     signalr-negotiate / websocket-upgrade / prefetch / bot-raw /
    ///     unknown</c>) is in the list. Example: <c>mode_in: [navigation]</c>
    ///     on <c>/admin/**</c> rejects bare-API hits that didn't come from a
    ///     real page load; <c>mode_in: [xhr, signalr-negotiate]</c> on
    ///     <c>/api/**</c> rejects navigations to API endpoints. Empty/null
    ///     matches any mode (the legacy behaviour). Membership is case-
    ///     insensitive.
    /// </summary>
    public List<string>? ModeIn { get; set; }

    /// <summary>Named action policy to dispatch on match (block / throttle-stealth / challenge / logonly).</summary>
    public string Action { get; set; } = "";

    /// <summary>Optional HTTP status code for block actions. Default depends on the action.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Optional reason -- surfaced in logs + dashboard.</summary>
    public string? Reason { get; set; }
}
