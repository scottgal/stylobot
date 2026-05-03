using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed class LocalLlmSemanticContributor : IEmailScoreContributor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EmailScoringOptions _options;
    private readonly HttpClient _httpClient;

    public LocalLlmSemanticContributor(IOptions<EmailScoringOptions> options)
    {
        _options = options.Value;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(500, _options.LocalLlm.TimeoutMs))
        };
    }

    public string Name => "LocalLlmSemantic";

    public async Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(
        EmailEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var cfg = _options.LocalLlm;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Endpoint) || string.IsNullOrWhiteSpace(cfg.Model))
        {
            return [];
        }

        var body = string.Join("\n", envelope.EnumerateBodies());
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        body = Truncate(body, Math.Max(512, cfg.MaxBodyChars));
        var prompt = BuildPrompt(envelope.Subject, envelope.From, envelope.To, body);
        try
        {
            var payload = new
            {
                model = cfg.Model,
                prompt,
                stream = false,
                format = "json",
                options = new
                {
                    temperature = 0.1
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!TryExtractLlmResult(raw, out var probability, out var reasons))
            {
                return [];
            }

            probability = Math.Clamp(probability, 0.0, 1.0);
            if (probability < cfg.MinSuspiciousProbability)
            {
                return [];
            }

            var delta = Math.Min(cfg.MaxScoreDelta, Math.Max(0.05, probability * cfg.MaxScoreDelta));
            var reasonText = reasons.Count > 0
                ? $"Local semantic model flagged suspicious intent ({probability:F2}): {string.Join("; ", reasons.Take(3))}"
                : $"Local semantic model flagged suspicious intent ({probability:F2})";

            return
            [
                new ScoreContribution(
                    Name,
                    "Semantic",
                    delta,
                    1.15,
                    reasonText)
            ];
        }
        catch
        {
            return [];
        }
    }

    private static bool TryExtractLlmResult(string rawResponse, out double suspiciousProbability, out List<string> reasons)
    {
        suspiciousProbability = 0;
        reasons = [];

        using var outer = JsonDocument.Parse(rawResponse);

        if (TryReadResultObject(outer.RootElement, out suspiciousProbability, out reasons))
        {
            return true;
        }

        if (outer.RootElement.TryGetProperty("response", out var responseTextNode) &&
            responseTextNode.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(responseTextNode.GetString()))
        {
            using var inner = JsonDocument.Parse(responseTextNode.GetString()!);
            return TryReadResultObject(inner.RootElement, out suspiciousProbability, out reasons);
        }

        if (outer.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentNode) &&
                contentNode.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(contentNode.GetString()))
            {
                using var inner = JsonDocument.Parse(contentNode.GetString()!);
                return TryReadResultObject(inner.RootElement, out suspiciousProbability, out reasons);
            }
        }

        return false;
    }

    private static bool TryReadResultObject(JsonElement node, out double suspiciousProbability, out List<string> reasons)
    {
        suspiciousProbability = 0;
        reasons = [];

        if (!node.TryGetProperty("suspiciousProbability", out var probabilityNode))
        {
            return false;
        }

        if (probabilityNode.ValueKind is not JsonValueKind.Number || !probabilityNode.TryGetDouble(out var probability))
        {
            return false;
        }

        suspiciousProbability = probability;
        if (node.TryGetProperty("reasons", out var reasonsNode) && reasonsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var reason in reasonsNode.EnumerateArray())
            {
                if (reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString()))
                {
                    reasons.Add(reason.GetString()!);
                }
            }
        }

        return true;
    }

    private static string BuildPrompt(string? subject, string? from, IReadOnlyList<string> to, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an email abuse classifier.");
        sb.AppendLine("Return STRICT JSON only:");
        sb.AppendLine("{\"suspiciousProbability\":0.0,\"reasons\":[\"short reason\"]}");
        sb.AppendLine("Use suspiciousProbability in [0,1].");
        sb.AppendLine("Focus on phishing, scams, social engineering, malware delivery, or spam intent.");
        sb.AppendLine();
        sb.AppendLine($"Subject: {subject ?? string.Empty}");
        sb.AppendLine($"From: {from ?? string.Empty}");
        sb.AppendLine($"To: {string.Join(", ", to.Take(10))}");
        sb.AppendLine("Body:");
        sb.AppendLine(body);
        return sb.ToString();
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars];
    }
}
