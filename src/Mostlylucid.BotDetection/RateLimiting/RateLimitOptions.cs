namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Root of the rate-limit config. Bound from
///     <c>BotDetection:RateLimits</c> in appsettings or any YAML loader.
///     The shape mirrors the spec at
///     <c>docs/superpowers/specs/2026-05-25-rate-limit-core-design.md</c>:
///     a global scope at the top with nested Domain / Subdomain / Endpoint
///     / Method scopes; each scope can declare its own rules and either
///     inherit from its parent or replace.
///     <para>
///     FOSS resolves <see cref="Endpoints"/> and per-endpoint
///     <see cref="RateLimitScopeNode.Methods"/> only. Commercial walks
///     <see cref="Domains"/> / <see cref="RateLimitScopeNode.Subdomains"/>
///     additionally. The options shape is identical -- gating is at
///     resolution time, not at config-load time, so an operator can prepare
///     a commercial config in a FOSS deploy and toggle the licence to
///     activate the upper tiers.
///     </para>
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "BotDetection:RateLimits";

    /// <summary>Master switch. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Global rules. Apply unless a more-specific scope overrides them.
    ///     The root scope is special: <c>Inherit</c> on it has no effect (no
    ///     parent to inherit from).
    /// </summary>
    public Dictionary<string, RateLimitRule> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Per-domain scopes (commercial). FOSS resolution skips this block.
    /// </summary>
    public Dictionary<string, RateLimitScopeNode> Domains { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Per-endpoint scopes. The dictionary key is a path glob -- exact, or
    ///     prefix glob ending in <c>*</c>. Walked at resolution time; first
    ///     glob match wins (declaration order in YAML).
    /// </summary>
    public Dictionary<string, RateLimitScopeNode> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Idle-bucket sweep interval. Buckets untouched for longer than
    ///     <see cref="IdleEvictionThresholdSeconds"/> are dropped on each
    ///     sweep so a flood of one-shot fingerprints doesn't grow the
    ///     in-memory state unbounded. Default 60s.
    /// </summary>
    public int IdleEvictionSweepSeconds { get; set; } = 60;

    /// <summary>
    ///     Drop buckets that have not been acquired for this many seconds.
    ///     Default 600 (10 minutes). Spec's tentative answer for the
    ///     "10x refill interval" guidance was 10 minutes.
    /// </summary>
    public int IdleEvictionThresholdSeconds { get; set; } = 600;
}

/// <summary>
///     A scope in the rate-limit tree. Recursive: a Domain has Subdomains
///     and Endpoints; an Endpoint has Methods; a Method is a leaf. Every
///     level can declare its own <see cref="Limits"/> dictionary.
/// </summary>
public sealed class RateLimitScopeNode
{
    /// <summary>
    ///     When true (default): parent's limits also apply UNLESS this scope
    ///     redefines a rule by name (in which case this scope's version
    ///     replaces the parent's for that name). When false: this scope
    ///     replaces the parent ENTIRELY -- only the rules declared here apply,
    ///     parent rules are dropped.
    /// </summary>
    public bool Inherit { get; set; } = true;

    public Dictionary<string, RateLimitRule> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commercial-only nested subdomain scopes.</summary>
    public Dictionary<string, RateLimitScopeNode> Subdomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Endpoint scopes nested under a domain or subdomain.</summary>
    public Dictionary<string, RateLimitScopeNode> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-HTTP-method scopes nested under an endpoint. Keys: <c>GET</c>, <c>POST</c>, ...</summary>
    public Dictionary<string, RateLimitScopeNode> Methods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     A single rate-limit rule: a set of subjects, a request-rate config,
///     an optional data-rate config, and the action to dispatch when over
///     budget.
/// </summary>
public sealed class RateLimitRule
{
    /// <summary>
    ///     Subjects that key the bucket. The rule fires only when EVERY
    ///     subject is present in the request's subject set (AND semantics).
    /// </summary>
    public List<RateLimitSubjectSpec> Subjects { get; set; } = new();

    /// <summary>Token-bucket request-rate config. Required.</summary>
    public RateLimitRequestRate RequestRate { get; set; } = new();

    /// <summary>Optional leaky-bucket data-rate config. Null = no data-rate cap.</summary>
    public RateLimitDataRate? DataRate { get; set; }

    /// <summary>
    ///     Action policy name to dispatch when the request bucket is empty.
    ///     Resolves through the existing <c>IActionPolicyRegistry</c>.
    /// </summary>
    public string OverLimitAction { get; set; } = "throttle-status";

    /// <summary>Operator-supplied reason; surfaced in logs and the dashboard policy tab.</summary>
    public string? Reason { get; set; }
}

public sealed class RateLimitRequestRate
{
    public int RequestsPerMinute { get; set; } = 60;
    public int BurstSize { get; set; } = 10;
}

public sealed class RateLimitDataRate
{
    public long BytesPerSecond { get; set; }
}

/// <summary>
///     Single-subject or list-of-values predicate. Operators write either
///     <c>{ Type: BotType, Value: Scraper }</c> for a single value or
///     <c>{ Type: Country, Values: [RU, BY] }</c> for an OR list.
/// </summary>
public sealed class RateLimitSubjectSpec
{
    public RateLimitSubjectKind Type { get; set; }
    public string? Value { get; set; }
    public List<string> Values { get; set; } = new();

    /// <summary>Returns the effective value list -- merges <see cref="Value"/> + <see cref="Values"/>.</summary>
    public IReadOnlyList<string> Effective()
    {
        if (!string.IsNullOrEmpty(Value) && Values.Count == 0) return new[] { Value };
        if (string.IsNullOrEmpty(Value)) return Values;
        var merged = new List<string>(Values.Count + 1) { Value };
        merged.AddRange(Values);
        return merged;
    }
}
