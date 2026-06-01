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
                // Only fall back to the method-parameter defaults when the bound
                // configuration left the property empty. The previous shape
                // overwrote config-supplied values (BotDetection__AiDetection__
                // Ollama__Endpoint etc.) with the parameter defaults, so the
                // provider always pointed at http://localhost:11434 regardless of
                // what the operator set.
                if (string.IsNullOrWhiteSpace(opts.Endpoint)) opts.Endpoint = endpoint;
                if (string.IsNullOrWhiteSpace(opts.Model)) opts.Model = model;
                configure?.Invoke(opts);
            });

        services.AddHttpClient("stylobot-ollama");
        services.TryAddSingleton<ILlmProvider, OllamaLlmProvider>();

        services.AddStylobotLlmServices();

        return services;
    }
}
