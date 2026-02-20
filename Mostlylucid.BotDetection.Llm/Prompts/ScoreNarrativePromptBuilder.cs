using System.Text;

namespace Mostlylucid.BotDetection.Llm.Prompts;

/// <summary>
///     Builds the prompt for explaining a significant score change in plain English.
/// </summary>
public static class ScoreNarrativePromptBuilder
{
    public static string Build(
        string signature,
        double previousScore,
        double newScore,
        IReadOnlyDictionary<string, object?>? signals = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a security analyst. A visitor's bot probability score changed significantly.");
        sb.AppendLine("Explain the change in 1-2 plain English sentences for a dashboard user.");
        sb.AppendLine();
        sb.AppendLine($"Signature: {signature[..Math.Min(16, signature.Length)]}...");
        sb.AppendLine($"Previous score: {previousScore:F2}");
        sb.AppendLine($"New score: {newScore:F2}");
        sb.AppendLine($"Direction: {(newScore > previousScore ? "MORE suspicious" : "LESS suspicious")}");

        if (signals is { Count: > 0 })
        {
            sb.AppendLine();
            var budget = 600;
            foreach (var (key, value) in signals)
            {
                if (budget <= 0) break;
                if (value == null) continue;
                var line = $"{key}: {value}";
                if (line.Length > 100) line = line[..100] + "...";
                sb.AppendLine(line);
                budget -= line.Length;
            }
        }

        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object:");
        sb.AppendLine("""{"narrative": "Plain English explanation of why the score changed"}""");

        return sb.ToString();
    }
}
