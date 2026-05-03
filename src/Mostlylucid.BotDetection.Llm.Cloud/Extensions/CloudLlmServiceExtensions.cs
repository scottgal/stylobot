using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Llm.Extensions;

namespace Mostlylucid.BotDetection.Llm.Cloud.Extensions;

/// <summary>
///     Registers cloud LLM provider based on preset name.
///     Automatically selects the right implementation (OpenAI-compatible, Anthropic, or Gemini).
/// </summary>
public static class CloudLlmServiceExtensions
{
    /// <summary>
    ///     Add a cloud LLM provider by preset name.
    ///     Supports: openai, anthropic, gemini, groq, mistral, deepseek, together,
    ///     fireworks, openrouter, azure, ollama.
    /// </summary>
    public static IServiceCollection AddStylobotCloudLlm(
        this IServiceCollection services,
        string provider,
        string? apiKey = null,
        string? model = null,
        string? baseUrl = null)
    {
        // Read API key from env if not provided
        apiKey ??= Environment.GetEnvironmentVariable("STYLOBOT_LLM_KEY") ?? "";

        services.AddOptions<CloudLlmOptions>()
            .BindConfiguration("BotDetection:AiDetection:Cloud")
            .Configure(opts =>
            {
                opts.Provider = provider;
                if (!string.IsNullOrEmpty(apiKey)) opts.ApiKey = apiKey;
                if (model != null) opts.Model = model;
                if (baseUrl != null) opts.BaseUrl = baseUrl;
            });

        services.AddHttpClient("stylobot-llm");

        // Select implementation based on provider's auth style
        if (ProviderPresets.All.TryGetValue(provider, out var preset))
        {
            switch (preset.Auth)
            {
                case AuthStyle.Anthropic:
                    services.TryAddSingleton<ILlmProvider, AnthropicLlmProvider>();
                    break;
                case AuthStyle.GeminiQuery:
                    services.TryAddSingleton<ILlmProvider, GeminiLlmProvider>();
                    break;
                default:
                    // Bearer, AzureApiKey, None - all use OpenAI-compatible API
                    services.TryAddSingleton<ILlmProvider, OpenAiCompatibleLlmProvider>();
                    break;
            }
        }
        else
        {
            // Unknown preset - assume OpenAI-compatible
            services.TryAddSingleton<ILlmProvider, OpenAiCompatibleLlmProvider>();
        }

        // Register shared LLM services (classification, bot naming, etc.)
        services.AddStylobotLlmServices();

        return services;
    }
}
