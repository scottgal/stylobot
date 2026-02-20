using System.Text;

namespace Mostlylucid.BotDetection.Llm.Prompts;

/// <summary>
///     Builds the prompt for bot name + description synthesis.
///     Extracted from LlamaSharpBotNameSynthesizer.BuildPrompt().
/// </summary>
public static class BotNamingPromptBuilder
{
    public static string Build(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        IReadOnlyList<string>? recentlyUsedNames = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a creative bot naming expert. Analyze these detection signals and generate a unique, descriptive, and slightly humorous bot name.");
        sb.AppendLine("The name should reflect the bot's behavior/origin in a witty way (like 'Captain Crawlspace', 'The Headless Harvester', 'Senor Scrape-a-Lot', 'ByteNinja 3000').");
        sb.AppendLine();

        const int maxSignalChars = 1200;
        var signalBudget = maxSignalChars;

        void TryAddSignal(string label, object? value)
        {
            if (value == null || signalBudget <= 0) return;
            var line = $"{label}: {value}";
            if (line.Length > 200) line = line[..200] + "...";
            if (line.Length <= signalBudget)
            {
                sb.AppendLine(line);
                signalBudget -= line.Length;
            }
        }

        signals.TryGetValue("ua.bot_type", out var botType);
        TryAddSignal("Bot Type", botType);
        signals.TryGetValue("ua.bot_name", out var knownName);
        TryAddSignal("Known Bot", knownName);
        signals.TryGetValue("detection.useragent.source", out var ua);
        TryAddSignal("UserAgent", ua);
        signals.TryGetValue("detection.ip.type", out var ipType);
        TryAddSignal("IP Type", ipType);
        signals.TryGetValue("ip.is_datacenter", out var dc);
        TryAddSignal("Datacenter", dc);
        signals.TryGetValue("geo.country_code", out var country);
        TryAddSignal("Country", country);
        signals.TryGetValue("detection.behavioral.rate_limit_violations", out var rateViolations);
        TryAddSignal("Rate Violations", rateViolations);
        signals.TryGetValue("detection.correlation.primary_behavior", out var behavior);
        TryAddSignal("Behavior", behavior);
        signals.TryGetValue("detection.heuristic.probability", out var heurProb);
        TryAddSignal("Bot Probability", heurProb);
        signals.TryGetValue("tls.ja3_hash", out var ja3);
        TryAddSignal("TLS Fingerprint", ja3);
        signals.TryGetValue("waveform.traversal_pattern", out var traversal);
        TryAddSignal("Traversal", traversal);

        var sigCount = 0;
        foreach (var (key, value) in signals)
        {
            if (sigCount >= 5) break;
            if (key.StartsWith("signature.") && value != null)
            {
                TryAddSignal(key, value);
                sigCount++;
            }
        }

        if (!string.IsNullOrEmpty(context) && signalBudget > 50)
        {
            sb.AppendLine();
            var ctxLine = $"Additional Context: {context}";
            if (ctxLine.Length > 200) ctxLine = ctxLine[..200] + "...";
            sb.AppendLine(ctxLine);
        }

        if (recentlyUsedNames is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"Do NOT reuse these names: {string.Join(", ", recentlyUsedNames)}");
        }

        sb.AppendLine();
        sb.AppendLine("""
            Respond with ONLY this JSON:
            {
              "name": "Funny Bot Name (2-5 words)",
              "description": "What this bot does (1 sentence)"
            }
            """);

        return sb.ToString();
    }
}
