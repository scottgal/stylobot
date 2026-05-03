using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Llm.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Ollama.Extensions;

/// <summary>
///     Registers the Ollama HTTP LLM provider and all shared LLM services.
/// </summary>
public static class OllamaServiceExtensions
{
    /// <summary>
    ///     Add Ollama HTTP LLM provider (external server, GPU-capable).
    /// </summary>
    public static IServiceCollection AddStylobotOllama(
        this IServiceCollection services,
        string endpoint = LlmDefaults.DefaultEndpoint,
        string model = LlmDefaults.DefaultModel,
        Action<OllamaProviderOptions>? configure = null)
    {
        services.AddOptions<OllamaProviderOptions>()
            .BindConfiguration("BotDetection:AiDetection:Ollama")
            .Configure(opts =>
            {
                opts.Endpoint = endpoint;
                opts.Model = model;
                configure?.Invoke(opts);
            });

        services.AddHttpClient("stylobot-ollama");
        services.TryAddSingleton<ILlmProvider, OllamaLlmProvider>();

        services.AddStylobotLlmServices();

        return services;
    }
}
