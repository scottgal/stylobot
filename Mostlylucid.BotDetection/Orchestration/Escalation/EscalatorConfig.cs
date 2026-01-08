namespace Mostlylucid.BotDetection.Orchestration.Escalation;

/// <summary>
///     Configuration for escalator decisions with pattern-based signal matching.
/// </summary>
public sealed class EscalatorConfig
{
    /// <summary>Risk threshold for early escalation (default: 0.7)</summary>
    public double EarlyEscalationThreshold { get; init; } = 0.7;

    /// <summary>Score threshold for storage (default: 0.6)</summary>
    public double StoreThreshold { get; init; } = 0.6;

    /// <summary>Score threshold for alerts (default: 0.8)</summary>
    public double AlertThreshold { get; init; } = 0.8;

    /// <summary>Store honeypot requests (default: true)</summary>
    public bool StoreHoneypotRequests { get; init; } = true;

    /// <summary>Alert on honeypot hits (default: true)</summary>
    public bool AlertOnHoneypot { get; init; } = true;

    /// <summary>Request signal patterns for dynamic extraction</summary>
    public Dictionary<string, string> RequestPatterns { get; init; } = new()
    {
        ["risk"] = "request.*.risk",
        ["honeypot"] = "request.*.honeypot",
        ["datacenter"] = "request.ip.*center",
        ["path"] = "request.path",
        ["method"] = "request.method"
    };

    /// <summary>Response signal patterns for dynamic extraction</summary>
    public Dictionary<string, string> ResponsePatterns { get; init; } = new()
    {
        ["score"] = "response.*.score",
        ["status"] = "response.status*",
        ["bytes"] = "response.*.bytes"
    };

    /// <summary>Trigger signal patterns to extract</summary>
    public Dictionary<string, string> TriggerPatterns { get; init; } = new()
    {
        ["request_triggers"] = "request.trigger.*",
        ["response_triggers"] = "response.trigger.*",
        ["high_risk"] = "*.high_risk"
    };

    /// <summary>Escalation rules for request analysis</summary>
    public List<EscalationRule> EscalationRules { get; init; } = new()
    {
        new EscalationRule
        {
            Name = "honeypot_immediate",
            Priority = 100,
            Condition = "honeypot == true",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Honeypot hit - immediate escalation"
        },
        new EscalationRule
        {
            Name = "high_risk_early",
            Priority = 90,
            Condition = "risk > 0.8",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "High risk: {risk}"
        }
    };

    /// <summary>Escalation rules for operation completion</summary>
    public List<EscalationRule> OperationEscalationRules { get; init; } = new()
    {
        new EscalationRule
        {
            Name = "honeypot_404",
            Priority = 100,
            Condition = "honeypot == true && status == 404",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Honeypot 404: {path}"
        },
        new EscalationRule
        {
            Name = "high_combined_score",
            Priority = 90,
            Condition = "combined_score > 0.85",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "High combined score: {combined_score}"
        }
    };
}