using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck.Extensions;

public static class LlmHolodeckExtensions
{
    public static IServiceCollection AddLlmHolodeck(
        this IServiceCollection services,
        Action<HolodeckLlmOptions>? configure = null)
    {
        services.AddOptions<HolodeckLlmOptions>()
            .BindConfiguration(HolodeckLlmOptions.SectionName)
            .Configure(opts => configure?.Invoke(opts));

        services.AddSingleton<IHolodeckResponder, LlmHolodeckResponder>();

        return services;
    }
}
