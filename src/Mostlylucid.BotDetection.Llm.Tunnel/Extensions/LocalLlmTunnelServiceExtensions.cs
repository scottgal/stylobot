using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Llm;

namespace Mostlylucid.BotDetection.Llm.Tunnel.Extensions;

public static class LocalLlmTunnelServiceExtensions
{
    /// <summary>
    /// Register tunnel client services (remote ILlmProvider + node registry).
    /// Call this on the Stylobot site that consumes remote tunnel nodes.
    /// </summary>
    public static IServiceCollection AddLocalLlmTunnelClient(
        this IServiceCollection services,
        Action<LocalLlmTunnelOptions>? configure = null)
    {
        services.AddOptions<LocalLlmTunnelOptions>()
            .BindConfiguration(LocalLlmTunnelOptions.SectionName);
        if (configure != null)
            services.PostConfigure<LocalLlmTunnelOptions>(configure);

        services.TryAddSingleton<ILlmNodeRegistry, InMemoryLlmNodeRegistry>();
        services.AddHttpClient("stylobot-llm-tunnel-client");
        services.TryAddSingleton<LocalLlmTunnelCrypto>();
        services.TryAddSingleton<ILlmProvider, LocalLlmTunnelClientProvider>();
        services.AddHostedService<LlmTunnelConfigImporter>();

        return services;
    }

    /// <summary>
    /// Register local agent services (probe + crypto) for the console llmtunnel command.
    /// </summary>
    public static IServiceCollection AddLocalLlmTunnelAgent(
        this IServiceCollection services,
        LocalLlmAgentContext agentContext)
    {
        services.AddSingleton(agentContext);
        services.AddSingleton<LocalLlmTunnelCrypto>();
        services.AddSingleton<LocalLlmProviderProbe>();

        return services;
    }
}
