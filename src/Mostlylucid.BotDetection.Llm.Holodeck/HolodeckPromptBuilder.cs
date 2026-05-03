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
        sb.AppendLine($"- HTTP method: {context.Method}");
        sb.AppendLine($"- Request path: {context.Path}");

        if (hints?.ProductContext is { Count: > 0 })
            foreach (var (key, value) in hints.ProductContext)
                sb.AppendLine($"- {key}: {value}");

        sb.AppendLine();
        sb.AppendLine($"Generate the {format} response for {context.Method} {context.Path}");
        return sb.ToString();
    }
}
