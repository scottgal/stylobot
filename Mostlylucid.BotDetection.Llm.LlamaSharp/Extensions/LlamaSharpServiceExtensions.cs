using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Llm.Extensions;

namespace Mostlylucid.BotDetection.Llm.LlamaSharp.Extensions;

/// <summary>
///     Registers the LlamaSharp LLM provider and all shared LLM services.
/// </summary>
public static class LlamaSharpServiceExtensions
{
    /// <summary>
    ///     Add in-process LlamaSharp LLM provider (CPU, zero external deps).
    /// </summary>
    public static IServiceCollection AddStylobotLlamaSharp(
        this IServiceCollection services,
        Action<LlamaSharpProviderOptions>? configure = null)
    {
        services.AddOptions<LlamaSharpProviderOptions>()
            .BindConfiguration("BotDetection:AiDetection:LlamaSharp")
            .Configure(opts => configure?.Invoke(opts));

        services.TryAddSingleton<ILlmProvider, LlamaSharpLlmProvider>();

        services.AddStylobotLlmServices();

        return services;
    }
}
