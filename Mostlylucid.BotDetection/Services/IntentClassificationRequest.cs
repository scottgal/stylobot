using System.Text;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Request for background LLM intent classification.
///     Contains pre-built session summary and intent features for the LLM to classify.
/// </summary>
public sealed record IntentClassificationRequest
{
    public required string RequestId { get; init; }
    public required string PrimarySignature { get; init; }
    public required float[] IntentVector { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public required string SessionSummary { get; init; }
    public required double HeuristicThreatScore { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
///     Builds prompts for LLM intent classification from detection signals.
/// </summary>
public static class IntentPromptBuilder
{
    private static readonly string[] Categories =
        ["browsing", "scraping", "scanning", "attacking", "reconnaissance", "monitoring", "abuse"];

    /// <summary>
    ///     Build the session summary text for LLM classification.
    /// </summary>
    public static string BuildSessionSummary(IReadOnlyDictionary<string, object> signals, string path)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== Session Activity Summary ===");
        sb.Append("Current path: ").AppendLine(path);

        // Attack signals
        if (signals.TryGetValue(Models.SignalKeys.AttackDetected, out var atk) && atk is true)
        {
            sb.Append("Attack detected: ");
            if (signals.TryGetValue(Models.SignalKeys.AttackCategories, out var cats))
                sb.Append(cats);
            sb.AppendLine();
            if (signals.TryGetValue(Models.SignalKeys.AttackSeverity, out var sev))
                sb.Append("  Severity: ").AppendLine(sev?.ToString());
        }

        // Response history
        if (signals.TryGetValue(Models.SignalKeys.ResponseTotalResponses, out var total))
            sb.Append("Total responses: ").AppendLine(total?.ToString());
        if (signals.TryGetValue(Models.SignalKeys.ResponseCount404, out var c404))
            sb.Append("404 responses: ").AppendLine(c404?.ToString());
        if (signals.TryGetValue(Models.SignalKeys.ResponseUnique404Paths, out var u404))
            sb.Append("Unique 404 paths: ").AppendLine(u404?.ToString());
        if (signals.TryGetValue(Models.SignalKeys.ResponseHoneypotHits, out var hp) &&
            hp is int hpInt && hpInt > 0)
            sb.Append("Honeypot hits: ").AppendLine(hpInt.ToString());
        if (signals.TryGetValue(Models.SignalKeys.ResponseAuthFailures, out var af) &&
            af is int afInt && afInt > 0)
            sb.Append("Auth failures: ").AppendLine(afInt.ToString());

        // Transport
        if (signals.TryGetValue(Models.SignalKeys.TransportClass, out var tc))
            sb.Append("Transport: ").AppendLine(tc?.ToString());
        if (signals.TryGetValue(Models.SignalKeys.TransportProtocolClass, out var pc))
            sb.Append("Protocol: ").AppendLine(pc?.ToString());

        // Temporal
        if (signals.TryGetValue(Models.SignalKeys.WaveformBurstDetected, out var burst) && burst is true)
            sb.AppendLine("Burst detected: yes");

        // Stream abuse
        if (signals.TryGetValue(Models.SignalKeys.StreamHandshakeStorm, out var storm) && storm is true)
            sb.AppendLine("Handshake storm: yes");
        if (signals.TryGetValue(Models.SignalKeys.StreamCrossEndpointMixing, out var mix) && mix is true)
            sb.AppendLine("Cross-endpoint mixing: yes");

        // ATO signals
        if (signals.TryGetValue(Models.SignalKeys.AtoBruteForce, out var bf) && bf is true)
            sb.AppendLine("Brute force detected: yes");
        if (signals.TryGetValue(Models.SignalKeys.AtoCredentialStuffing, out var cs) && cs is true)
            sb.AppendLine("Credential stuffing detected: yes");

        return sb.ToString();
    }

    /// <summary>
    ///     Build the LLM prompt for intent classification.
    /// </summary>
    public static string BuildPrompt(string sessionSummary)
    {
        return $$"""
            You are a security analyst classifying the intent of a web session.
            Given the following session activity data, determine:
            1. How threatening this session is (0.0 = completely benign, 1.0 = actively attacking)
            2. The primary intent category
            3. Brief reasoning

            {{sessionSummary}}

            Respond with ONLY valid JSON (no markdown, no explanation):
            {"threat": 0.85, "category": "scanning", "reasoning": "Systematic probing of admin panels and config files"}

            Categories: {{string.Join(", ", Categories)}}
            """;
    }
}
