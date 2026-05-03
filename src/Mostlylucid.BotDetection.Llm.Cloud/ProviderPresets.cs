using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Cloud;

/// <summary>
///     Built-in provider presets. Each preset defines a base URL, default model,
///     and authentication style. Users only need to supply an API key.
/// </summary>
public static class ProviderPresets
{
    public static readonly Dictionary<string, ProviderPreset> All = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI-compatible providers (all use /v1/chat/completions with Bearer auth)
        ["openai"] = new("https://api.openai.com", "gpt-4o-mini", AuthStyle.Bearer,
            "OpenAI - best value for bot classification (~$0.15/1M tokens)"),
        ["groq"] = new("https://api.groq.com/openai", "llama-3.3-70b-versatile", AuthStyle.Bearer,
            "Groq - extremely fast inference, free tier available"),
        ["mistral"] = new("https://api.mistral.ai", "mistral-small-latest", AuthStyle.Bearer,
            "Mistral - EU-hosted, good multilingual support"),
        ["deepseek"] = new("https://api.deepseek.com", "deepseek-chat", AuthStyle.Bearer,
            "DeepSeek - very cheap (~$0.07/1M tokens)"),
        ["together"] = new("https://api.together.xyz", "meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo", AuthStyle.Bearer,
            "Together AI - wide model selection, fast"),
        ["fireworks"] = new("https://api.fireworks.ai/inference", "accounts/fireworks/models/llama-v3p1-8b-instruct", AuthStyle.Bearer,
            "Fireworks AI - fast serverless inference"),
        ["openrouter"] = new("https://openrouter.ai/api", "openai/gpt-4o-mini", AuthStyle.Bearer,
            "OpenRouter - meta-provider, routes to cheapest available"),

        // Azure OpenAI (OpenAI-compatible but with different auth + URL structure)
        ["azure"] = new("", "", AuthStyle.AzureApiKey,
            "Azure OpenAI - enterprise Azure deployment (supply --llm-url and --model)"),

        // Non-OpenAI APIs (need specific provider implementations)
        ["anthropic"] = new("https://api.anthropic.com", "claude-haiku-4-5-20251001", AuthStyle.Anthropic,
            "Anthropic - excellent reasoning for threat classification (~$0.25/1M tokens)"),
        ["gemini"] = new("https://generativelanguage.googleapis.com", "gemini-2.0-flash", AuthStyle.GeminiQuery,
            "Google Gemini - free tier available, fast"),

        // Ollama via OpenAI-compatible API (for remote Ollama instances)
        // Local ollama uses the dedicated OllamaLlmProvider (AddStylobotOllama) via CLI --llm ollama
        ["ollama"] = new(LlmDefaults.DefaultEndpoint, LlmDefaults.DefaultModel, AuthStyle.None,
            "Ollama - local or remote, free, GPU-accelerated. OpenAI-compatible API"),
    };

    /// <summary>Resolve a preset by name, applying overrides from options.</summary>
    public static (string BaseUrl, string Model, AuthStyle Auth) Resolve(CloudLlmOptions options)
    {
        if (!All.TryGetValue(options.Provider, out var preset))
            preset = new(options.BaseUrl ?? LlmDefaults.DefaultEndpoint, options.Model ?? LlmDefaults.DefaultModel, AuthStyle.Bearer, "Custom");

        return (
            options.BaseUrl ?? preset.BaseUrl,
            options.Model ?? preset.DefaultModel,
            preset.Auth
        );
    }
}

public sealed record ProviderPreset(string BaseUrl, string DefaultModel, AuthStyle Auth, string Description);

public enum AuthStyle
{
    None,           // No auth (Ollama, local)
    Bearer,         // Authorization: Bearer <key> (OpenAI-compatible)
    AzureApiKey,    // api-key: <key> header (Azure OpenAI)
    Anthropic,      // x-api-key: <key> + anthropic-version header
    GeminiQuery     // API key in query string ?key=<key>
}
