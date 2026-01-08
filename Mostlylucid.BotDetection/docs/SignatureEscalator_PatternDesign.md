# Signature Escalator: Pattern-Based Design

## Principle: Dynamic, Not Hardcoded

The SignatureEscalatorAtom uses **pattern matching** instead of hardcoded signal names. This makes it configurable and
extensible.

## Pattern Matcher Usage

```csharp
// Instead of hardcoded:
var risk = GetSignal<double>("request.risk") ?? 0.0;  // ❌ HARDCODED

// Use pattern matcher:
var signals = ExtractSignals(_requestPatterns);  //  ✅ PATTERN-BASED
var risk = signals.GetValueOrDefault("risk", 0.0);
```

## Configuration Structure

```yaml
escalator:
  # Define patterns for signal extraction
  request_patterns:
    risk: "request.*.risk"           # Matches: request.detector.risk, request.heuristic.risk, etc.
    honeypot: "request.*.honeypot"   # Matches any honeypot signal
    datacenter: "request.ip.*center" # Matches: datacenter, datacentre, etc.
    path: "request.path"
    method: "request.method"

  response_patterns:
    score: "response.*.score"        # Matches: response.detector.score, response.analysis.score
    status: "response.status*"       # Matches: response.status, response.status.code
    bytes: "response.*.bytes"

  trigger_patterns:
    - "request.trigger.*"            # All request triggers
    - "response.trigger.*"           # All response triggers
    - "*.high_risk"                  # Any high risk signal

  # Escalation rules (pattern-driven)
  escalation_rules:
    - name: "honeypot_immediate"
      priority: 100
      condition: "honeypot == true"
      should_store: true
      should_alert: true
      reason: "Honeypot hit - immediate escalation"

    - name: "high_risk_early"
      priority: 90
      condition: "risk > 0.8"
      should_store: true
      should_alert: true
      reason: "High risk: {risk}"

    - name: "datacenter_thorough"
      priority: 70
      condition: "datacenter != null && risk > 0.5"
      should_store: false
      should_alert: false
      reason: "Datacenter IP: {datacenter}, risk: {risk}"

  operation_escalation_rules:
    - name: "honeypot_404"
      priority: 100
      condition: "honeypot == true && status == 404"
      should_store: true
      should_alert: true
      reason: "Honeypot 404: {path}"

    - name: "high_combined_score"
      priority: 90
      condition: "max(risk, score) > 0.85"
      should_store: true
      should_alert: true
      reason: "High combined score: {max(risk, score)}"

    - name: "scan_pattern"
      priority: 70
      condition: "status == 404 && risk > 0.6"
      should_store: true
      should_alert: false
      reason: "404 scan detected"
```

## Pattern Matcher Implementation

```csharp
public class SignalPatternMatcher
{
    private readonly Dictionary<string, string> _patterns;

    public SignalPatternMatcher(Dictionary<string, string> patterns)
    {
        _patterns = patterns;
    }

    /// <summary>
    ///     Extract signals matching configured patterns from sink.
    /// </summary>
    public Dictionary<string, object> ExtractFrom(SignalSink sink)
    {
        var extracted = new Dictionary<string, object>();

        foreach (var (name, pattern) in _patterns)
        {
            // Use SignalKey pattern matching (ephemeral feature)
            var matches = sink.Sense(new SignalKey(pattern));

            if (matches.Any())
            {
                // Get latest match for this pattern
                var latest = matches.OrderByDescending(e => e.Timestamp).First();
                extracted[name] = latest.Payload;
            }
        }

        return extracted;
    }
}
```

## Escalation Rule Implementation

```csharp
public class EscalationRule
{
    public required string Name { get; init; }
    public required int Priority { get; init; }
    public required string Condition { get; init; }  // Expression: "risk > 0.8"
    public required bool ShouldStore { get; init; }
    public required bool ShouldAlert { get; init; }
    public required string Reason { get; init; }  // Template: "High risk: {risk}"

    private readonly Func<Dictionary<string, object>, bool> _compiledCondition;
    private readonly Func<Dictionary<string, object>, string> _compiledReason;

    public EscalationRule()
    {
        // Compile condition expression (using expression trees or simple parser)
        _compiledCondition = CompileCondition(Condition);
        _compiledReason = CompileReason(Reason);
    }

    public bool ShouldEscalate(Dictionary<string, object> signals)
    {
        return _compiledCondition(signals);
    }

    public string BuildReason(Dictionary<string, object> signals)
    {
        return _compiledReason(signals);
    }

    private static Func<Dictionary<string, object>, bool> CompileCondition(string condition)
    {
        // TODO: Implement expression compiler
        // For now, simple parser:
        // "risk > 0.8" → signals.TryGetValue("risk", out var v) && v is double d && d > 0.8
        // "honeypot == true" → signals.TryGetValue("honeypot", out var v) && v is bool b && b == true

        return signals =>
        {
            // Simple implementation (production would use expression trees)
            if (condition.Contains(">"))
            {
                var parts = condition.Split('>');
                var key = parts[0].Trim();
                var threshold = double.Parse(parts[1].Trim());
                return signals.TryGetValue(key, out var val) && val is double d && d > threshold;
            }
            if (condition.Contains("=="))
            {
                var parts = condition.Split("==");
                var key = parts[0].Trim();
                var expected = parts[1].Trim();
                if (expected == "true")
                    return signals.TryGetValue(key, out var val) && val is bool b && b;
                if (expected == "false")
                    return signals.TryGetValue(key, out var val) && val is bool b && !b;
            }
            return false;
        };
    }

    private static Func<Dictionary<string, object>, string> CompileReason(string reason)
    {
        // Compile reason template: "High risk: {risk}" → interpolate from signals
        return signals =>
        {
            var result = reason;
            foreach (var (key, value) in signals)
            {
                result = result.Replace($"{{{key}}}", value.ToString());
            }
            return result;
        };
    }
}
```

## Usage Example

```csharp
// Configuration (from appsettings.json or database)
var config = new EscalatorConfig
{
    RequestPatterns = new Dictionary<string, string>
    {
        ["risk"] = "request.*.risk",          // Pattern!
        ["honeypot"] = "request.*.honeypot",
        ["datacenter"] = "request.ip.*"
    },

    EscalationRules = new List<EscalationRule>
    {
        new EscalationRule
        {
            Name = "honeypot_immediate",
            Priority = 100,
            Condition = "honeypot == true",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Honeypot hit"
        },
        new EscalationRule
        {
            Name = "high_risk",
            Priority = 90,
            Condition = "risk > 0.8",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "High risk: {risk}"
        }
    }
};

// In escalator atom
var matcher = new SignalPatternMatcher(config.RequestPatterns);
var signals = matcher.ExtractFrom(operationSink);

// Apply rules
foreach (var rule in config.EscalationRules.OrderByDescending(r => r.Priority))
{
    if (rule.ShouldEscalate(signals))
    {
        return new EscalationDecision
        {
            ShouldEscalate = true,
            Priority = rule.Priority,
            Reason = rule.BuildReason(signals),  // "High risk: 0.92"
            ShouldStore = rule.ShouldStore,
            ShouldAlert = rule.ShouldAlert
        };
    }
}
```

## Benefits

1. **No Hardcoding**: Signal names come from config, not code
2. **Pattern Matching**: `"request.*.risk"` matches any detector's risk signal
3. **Flexible Rules**: Add new escalation rules without code changes
4. **Expression-Based**: Conditions use simple expressions (`risk > 0.8`)
5. **Template Reasons**: `"High risk: {risk}"` interpolates from signals

## Ephemeral Pattern Matcher Integration

The `SignalKey` in ephemeral already supports wildcards:

```csharp
// SignalKey with pattern
var events = sink.Sense(new SignalKey("request.*.risk"));
// Matches:
// - request.heuristic.risk
// - request.ip.detector.risk
// - request.behavioral.risk
```

The escalator just wraps this with semantic names:

```
Pattern: "request.*.risk" → Named as: "risk"
```

So you extract with patterns, but use semantic names in rules!
