namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Post-detection policy rules. Same shape as
///     <see cref="EndpointPolicyRule"/> for the request-shape matchers, plus
///     four detection-output matchers that can only be evaluated after the
///     bot-detection pipeline has produced a result.
///     <para>
///     Pre-detection rules live in <see cref="EndpointPolicyOptions"/>
///     (<c>BotDetection:EndpointPolicies</c>); they decide block/throttle
///     by request shape alone. THIS section --
///     <c>BotDetection:DetectionPolicies</c> -- runs after the detection
///     pipeline so it can reference bot probability, confidence, type,
///     and threat band. Same first-match-wins evaluation, same action
///     registry dispatch.
///     </para>
/// </summary>
public sealed class DetectionPolicyOptions
{
    public const string SectionName = "BotDetection:DetectionPolicies";

    /// <summary>Master switch. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rules in declaration order. First-match wins.</summary>
    public List<DetectionPolicyRule> Rules { get; set; } = new();
}

/// <summary>
///     A single post-detection rule. Optional request-shape matchers
///     (inherited from <see cref="EndpointPolicyRule"/>'s vocabulary) and
///     optional detection-output matchers; omitted matchers wildcard.
///     A rule with no matchers at all matches every request and is mostly
///     a default-action sentinel.
/// </summary>
public sealed class DetectionPolicyRule
{
    /// <summary>Operator-supplied identifier. Used in logs and the dashboard policy tab.</summary>
    public string? Name { get; set; }

    /// <summary>Host glob (same syntax as <see cref="EndpointPolicyRule.Host"/>).</summary>
    public string? Host { get; set; }

    /// <summary>HTTP method or "ANY".</summary>
    public string? Method { get; set; }

    /// <summary>Path glob (same syntax as <see cref="EndpointPolicyRule.Path"/>).</summary>
    public string? Path { get; set; }

    /// <summary>
    ///     Comparison against the final bot probability (0.0 - 1.0). Syntax:
    ///     one operator (<c>&gt;= &lt;= &gt; &lt; == !=</c>) followed by a
    ///     decimal literal. Examples: <c>"&gt;= 0.7"</c>, <c>"&lt; 0.5"</c>.
    ///     Null matches any value.
    /// </summary>
    public string? BotProbability { get; set; }

    /// <summary>Same syntax as <see cref="BotProbability"/>, applied to the confidence value.</summary>
    public string? Confidence { get; set; }

    /// <summary>
    ///     Bot type (single value or list). Matched case-insensitively against
    ///     the detection result's BotType. Both <see cref="Type"/> and
    ///     <see cref="Types"/> are accepted so single-value YAML is concise.
    /// </summary>
    public string? Type { get; set; }
    public List<string>? Types { get; set; }

    /// <summary>Threat band (single value or list). Same semantics as <see cref="Type"/>.</summary>
    public string? Threat { get; set; }
    public List<string>? Threats { get; set; }

    /// <summary>Named action policy to dispatch on match. Required.</summary>
    public string Action { get; set; } = "";

    /// <summary>Operator-supplied reason; surfaced in logs and the dashboard.</summary>
    public string? Reason { get; set; }

    /// <summary>Returns the effective Type list: merges <see cref="Type"/> + <see cref="Types"/>.</summary>
    public IReadOnlyList<string> EffectiveTypes()
    {
        if (!string.IsNullOrEmpty(Type) && (Types is null || Types.Count == 0)) return new[] { Type };
        if (string.IsNullOrEmpty(Type)) return Types ?? (IReadOnlyList<string>)Array.Empty<string>();
        var merged = new List<string>((Types?.Count ?? 0) + 1) { Type };
        if (Types is not null) merged.AddRange(Types);
        return merged;
    }

    /// <summary>Returns the effective Threat list: merges <see cref="Threat"/> + <see cref="Threats"/>.</summary>
    public IReadOnlyList<string> EffectiveThreats()
    {
        if (!string.IsNullOrEmpty(Threat) && (Threats is null || Threats.Count == 0)) return new[] { Threat };
        if (string.IsNullOrEmpty(Threat)) return Threats ?? (IReadOnlyList<string>)Array.Empty<string>();
        var merged = new List<string>((Threats?.Count ?? 0) + 1) { Threat };
        if (Threats is not null) merged.AddRange(Threats);
        return merged;
    }
}
