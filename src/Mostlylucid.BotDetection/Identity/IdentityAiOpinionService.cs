using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Outcome of an operator-triggered AI opinion request from the dashboard.
///     <see cref="BotProbability"/> and <see cref="RiskBand"/> are populated only when
///     <see cref="Status"/> is <see cref="IdentityAiOpinionStatus.Ok"/>; every other
///     status is a named failure mode the dashboard surfaces verbatim.
/// </summary>
public sealed record IdentityAiOpinionResult(
    IdentityAiOpinionStatus Status,
    string FingerprintId,
    double? BotProbability,
    string? RiskBand,
    string? Reasoning,
    string? ErrorDetail);

public enum IdentityAiOpinionStatus
{
    Ok,
    IdentityDisabled,
    NotFound,
    NoLlmProvider,
    LlmNotReady,
    LlmError,
    ParseError
}

public static class IdentityAiOpinionStatusExtensions
{
    /// <summary>Header-friendly hyphenated form (e.g. <c>identity-disabled</c>).</summary>
    public static string ToHeaderValue(this IdentityAiOpinionStatus status) => status switch
    {
        IdentityAiOpinionStatus.Ok               => "ok",
        IdentityAiOpinionStatus.IdentityDisabled => "identity-disabled",
        IdentityAiOpinionStatus.NotFound         => "not-found",
        IdentityAiOpinionStatus.NoLlmProvider    => "no-llm-provider",
        IdentityAiOpinionStatus.LlmNotReady      => "llm-not-ready",
        IdentityAiOpinionStatus.LlmError         => "llm-error",
        IdentityAiOpinionStatus.ParseError       => "parse-error",
        _                                        => "unknown"
    };
}

/// <summary>
///     On-demand classifier invocation for a metastable fingerprint. The dashboard's
///     "Run AI" button posts to <c>/api/identities/{id}/run-ai</c>; the middleware
///     calls into this service, which builds a fingerprint-summary prompt, sends it
///     synchronously to the registered <c>ILlmProvider</c> (resolved by reflection so
///     core does not take a hard dependency on the optional Llm package), parses the
///     JSON response, and writes the verdict back to <c>fingerprints.cached_*</c>.
///
///     Returns a structured result rather than throwing — the dashboard's UX needs to
///     show "no provider available" or "parse error" as plain status, not a 500.
///
///     Future enhancements (not in this slice): pull the fingerprint's most recent
///     session from the session store and feed paths + dominant Markov state into the
///     prompt; pull the recent observation vector for tighter behavioural framing.
/// </summary>
public sealed class IdentityAiOpinionService
{
    private readonly ILogger<IdentityAiOpinionService> _logger;
    private readonly SqliteFingerprintStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly bool _enabled;

    public IdentityAiOpinionService(
        ILogger<IdentityAiOpinionService> logger,
        SqliteFingerprintStore store,
        IServiceProvider serviceProvider,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _serviceProvider = serviceProvider;
        _enabled = options.Value.Identity.Enabled;
    }

    public async Task<IdentityAiOpinionResult> RunAsync(string fingerprintId, CancellationToken ct = default)
    {
        if (!_enabled)
            return new IdentityAiOpinionResult(IdentityAiOpinionStatus.IdentityDisabled, fingerprintId, null, null, null,
                "Identity:Enabled is false; the metastable identity layer is dormant.");

        var fp = await _store.GetFingerprintAsync(fingerprintId, ct);
        if (fp is null)
            return new IdentityAiOpinionResult(IdentityAiOpinionStatus.NotFound, fingerprintId, null, null, null,
                "No fingerprint with that id exists.");

        var (provider, completeAsync, llmRequestType, isReadyProperty) = ResolveLlmProvider();
        if (provider is null || completeAsync is null || llmRequestType is null)
            return new IdentityAiOpinionResult(IdentityAiOpinionStatus.NoLlmProvider, fingerprintId, null, null, null,
                "No ILlmProvider is registered. Add one of the Mostlylucid.BotDetection.Llm.* packages and configure it.");

        // Provider exists but may not have its model loaded (LlamaSharp without the GGUF file,
        // Ollama without a pulled model, etc.). Surface that as a distinct status so the
        // operator can see exactly why nothing happened.
        if (isReadyProperty is not null && isReadyProperty.GetValue(provider) is bool isReady && !isReady)
            return new IdentityAiOpinionResult(IdentityAiOpinionStatus.LlmNotReady, fingerprintId, null, null, null,
                "ILlmProvider is registered but reports IsReady=false (model not loaded or initialization failed).");

        string raw;
        try
        {
            var prompt = BuildPrompt(fp);
            var request = Activator.CreateInstance(llmRequestType)!;
            llmRequestType.GetProperty("Prompt")!.SetValue(request, prompt);

            var task = (Task<string>)completeAsync.Invoke(provider, [request, ct])!;
            raw = await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity AI opinion: LLM call failed for {FingerprintId}", fingerprintId);
            return new IdentityAiOpinionResult(IdentityAiOpinionStatus.LlmError, fingerprintId, null, null, null, ex.Message);
        }

        var parsed = TryParse(raw);
        if (parsed is null)
            return new IdentityAiOpinionResult(IdentityAiOpinionStatus.ParseError, fingerprintId, null, null, null,
                $"LLM did not return parseable JSON. Raw response: {Truncate(raw, 200)}");

        await _store.UpdateCachedVerdictAsync(fingerprintId, parsed.Probability, parsed.RiskBand, ct);
        _logger.LogInformation(
            "Identity AI opinion: fingerprint {Id} -> prob={Prob:F2} band={Band} reason={Reason}",
            fingerprintId, parsed.Probability, parsed.RiskBand, parsed.Reason);

        return new IdentityAiOpinionResult(IdentityAiOpinionStatus.Ok, fingerprintId, parsed.Probability, parsed.RiskBand, parsed.Reason, null);
    }

    private (object? Provider, System.Reflection.MethodInfo? CompleteAsync, Type? LlmRequestType, System.Reflection.PropertyInfo? IsReadyProperty) ResolveLlmProvider()
    {
        var providerType = Type.GetType("Mostlylucid.BotDetection.Llm.ILlmProvider, Mostlylucid.BotDetection.Llm");
        var requestType = Type.GetType("Mostlylucid.BotDetection.Llm.LlmRequest, Mostlylucid.BotDetection.Llm");
        if (providerType is null || requestType is null) return (null, null, null, null);

        var provider = _serviceProvider.GetService(providerType);
        if (provider is null) return (null, null, null, null);

        var method = providerType.GetMethod("CompleteAsync");
        var isReady = providerType.GetProperty("IsReady");
        return (provider, method, requestType, isReady);
    }

    private static string BuildPrompt(Fingerprint fp) => $$"""
        You are a bot-detection classifier. Given a visitor's metastable behavioural fingerprint,
        decide whether they are a bot. Reply with ONLY a JSON object — no prose, no markdown.

        Fingerprint metadata:
        - inferred_client_type: {{fp.InferredClientType}} (confidence {{fp.InferredTypeConfidence:F2}})
        - archetype_origin: {{fp.ArchetypeOrigin ?? "(none)"}}
        - observation_count: {{fp.ObservationCount}}
        - centroid_maturity: {{fp.CentroidMaturity}}
        - correction_count: {{fp.CorrectionCount}}
        - first_seen_utc: {{fp.FirstSeen:O}}
        - last_seen_utc: {{fp.LastSeen:O}}
        - cached_bot_probability: {{fp.CachedBotProbability:F2}}
        - cached_risk_band: {{fp.CachedRiskBand ?? "(none)"}}

        Reply schema (verbatim, no extra keys, no trailing commas):
        {"is_bot": true|false, "confidence": 0.0..1.0, "risk_band": "Low"|"Medium"|"High"|"Critical", "reason": "<one sentence>"}
        """;

    private static ParsedOpinion? TryParse(string raw)
    {
        try
        {
            // LLMs sometimes wrap JSON in markdown fences; trim them.
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```"))
            {
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
                if (trimmed.EndsWith("```")) trimmed = trimmed[..^3].TrimEnd();
            }

            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var isBot = root.TryGetProperty("is_bot", out var isBotEl)
                && isBotEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                && isBotEl.GetBoolean();
            var confidence = root.TryGetProperty("confidence", out var confEl) && confEl.ValueKind is JsonValueKind.Number
                ? confEl.GetDouble()
                : 0.5;
            var band = root.TryGetProperty("risk_band", out var bandEl) && bandEl.ValueKind == JsonValueKind.String
                ? bandEl.GetString()
                : null;
            var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()
                : null;

            // Probability is the bot-direction score. is_bot=true → confidence; is_bot=false → 1 - confidence.
            // This matches the convention the rest of the pipeline uses (BotProbability is bot-direction).
            var probability = isBot ? Math.Clamp(confidence, 0, 1) : Math.Clamp(1 - confidence, 0, 1);
            return new ParsedOpinion(probability, band, reason ?? "(no reason supplied)");
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record ParsedOpinion(double Probability, string? RiskBand, string Reason);
}
