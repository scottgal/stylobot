using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Llm.Services;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Llm.Extensions;

/// <summary>
///     Registers common LLM services (prompt builders, parser, service wrappers).
///     Called by provider-specific extension methods — not directly by consumers.
/// </summary>
public static class LlmServiceExtensions
{
    /// <summary>
    ///     Register the shared LLM service layer.
    ///     Expects ILlmProvider to already be registered by a provider package.
    /// </summary>
    public static IServiceCollection AddStylobotLlmServices(this IServiceCollection services)
    {
        // Classification service
        services.TryAddSingleton<LlmClassificationService>();

        // Bot name synthesizer — replaces the old IBotNameSynthesizer registration
        services.RemoveAll<IBotNameSynthesizer>();
        services.AddSingleton<IBotNameSynthesizer, LlmBotNameSynthesizer>();

        // Score narrative service
        services.TryAddSingleton<IScoreNarrativeService, LlmScoreNarrativeService>();

        return services;
    }
}
