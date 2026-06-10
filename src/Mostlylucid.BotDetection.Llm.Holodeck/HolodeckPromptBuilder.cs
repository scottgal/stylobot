using System.Text;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

public static class HolodeckPromptBuilder
{
    public static string Build(
        PackResponseTemplate template,
        HolodeckRequestContext context,
        string? canary)
    {
        var sb = new StringBuilder(1024);
        var hints = template.ResponseHints;
        var format = hints?.ResponseFormat ?? "html";

        // Method and path are the bot's own bytes - the only attacker-controlled
        // text in this prompt. Newlines stripped + length-capped so a crafted
        // path can't open its own "Rules:" section or restate instructions.
        var method = SanitizeRequestValue(context.Method, 16);
        var path = SanitizeRequestValue(context.Path, 256);

        sb.AppendLine($"You are simulating a {context.PackFramework ?? "web"} {context.PackVersion ?? ""} installation.");
        if (!string.IsNullOrEmpty(context.PackPersonality))
            sb.AppendLine(context.PackPersonality);
        sb.AppendLine($"Generate a realistic {format} response.");
        sb.AppendLine();

        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY the response body, no explanation or markdown fencing");
        sb.AppendLine($"- Match the content type exactly: {template.ContentType}");
        sb.AppendLine($"- The response must be valid {format} that a real {context.PackFramework ?? "server"} would produce");

        if (!string.IsNullOrEmpty(canary))
        {
            sb.AppendLine($"- Embed this exact value naturally in the response: \"{canary}\"");
            sb.AppendLine("- Place it where a nonce, token, API key, or session value would appear");
            sb.AppendLine("- Do NOT label it or mark it as special");
        }

        sb.AppendLine();
        sb.AppendLine("Context:");
        if (!string.IsNullOrEmpty(hints?.EndpointDescription))
            sb.AppendLine($"- Endpoint: {hints.EndpointDescription}");
        if (!string.IsNullOrEmpty(hints?.BodySchema))
            sb.AppendLine($"- Expected structure: {hints.BodySchema}");
        sb.AppendLine($"- HTTP method: {method}");
        sb.AppendLine($"- Request path: {path}");

        if (hints?.ProductContext is { Count: > 0 })
            foreach (var (key, value) in hints.ProductContext)
                sb.AppendLine($"- {key}: {value}");

        sb.AppendLine();
        sb.AppendLine($"Generate the {format} response for {method} {path}");
        return sb.ToString();
    }

    private static string SanitizeRequestValue(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ');
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
}
