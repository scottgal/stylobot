namespace Mostlylucid.BotDetection.Orchestration.Escalation;

/// <summary>
///     Escalation rule with expression-based conditions.
///     Uses pattern matching for dynamic signal resolution.
/// </summary>
public sealed class EscalationRule
{
    private Func<Dictionary<string, object>, bool>? _compiledCondition;
    private Func<Dictionary<string, object>, string>? _compiledReason;
    public required string Name { get; init; }
    public required int Priority { get; init; }
    public required string Condition { get; init; } // Expression: "risk > 0.8"
    public required bool ShouldStore { get; init; }
    public required bool ShouldAlert { get; init; }
    public required string Reason { get; init; } // Template: "High risk: {risk}"

    /// <summary>
    ///     Check if this rule should escalate based on signals.
    /// </summary>
    public bool ShouldEscalate(Dictionary<string, object> signals)
    {
        _compiledCondition ??= CompileCondition(Condition);
        return _compiledCondition(signals);
    }

    /// <summary>
    ///     Build reason string with interpolated signal values.
    /// </summary>
    public string BuildReason(Dictionary<string, object> signals)
    {
        _compiledReason ??= CompileReason(Reason);
        return _compiledReason(signals);
    }

    /// <summary>
    ///     Compile condition expression into a function.
    ///     TODO: Use expression trees for production.
    /// </summary>
    private static Func<Dictionary<string, object>, bool> CompileCondition(string condition)
    {
        return signals =>
        {
            // Simple parser for basic conditions
            // Production should use expression trees or a proper parser

            // Handle ">" comparisons
            if (condition.Contains(">"))
            {
                var parts = condition.Split('>');
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    if (double.TryParse(parts[1].Trim(), out var threshold))
                        if (signals.TryGetValue(key, out var val))
                            return val switch
                            {
                                double d => d > threshold,
                                int i => i > threshold,
                                float f => f > threshold,
                                _ => false
                            };
                }
            }

            // Handle "==" comparisons
            if (condition.Contains("=="))
            {
                var parts = condition.Split("==");
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var expected = parts[1].Trim();

                    if (!signals.TryGetValue(key, out var val))
                        return false;

                    if (expected == "true")
                        return val is bool b && b;
                    if (expected == "false")
                        return val is bool b && !b;

                    if (int.TryParse(expected, out var expectedInt))
                        return val is int i && i == expectedInt;

                    return val?.ToString() == expected.Trim('"', '\'');
                }
            }

            // Handle "&&" (AND) logic
            if (condition.Contains("&&"))
            {
                var parts = condition.Split("&&");
                return parts.All(part => CompileCondition(part.Trim())(signals));
            }

            // Handle "||" (OR) logic
            if (condition.Contains("||"))
            {
                var parts = condition.Split("||");
                return parts.Any(part => CompileCondition(part.Trim())(signals));
            }

            return false;
        };
    }

    /// <summary>
    ///     Compile reason template for string interpolation.
    /// </summary>
    private static Func<Dictionary<string, object>, string> CompileReason(string reason)
    {
        return signals =>
        {
            var result = reason;
            foreach (var (key, value) in signals) result = result.Replace($"{{{key}}}", value?.ToString() ?? "null");
            return result;
        };
    }
}