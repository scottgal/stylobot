using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Observability.OpenTelemetry;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    ///     Wires the StyloBot observability stack:
    ///     <list type="bullet">
    ///         <item>SerilogDetectionEventPublisher replaces the no-op IDetectionEventPublisher</item>
    ///         <item>OpenTelemetry tracing + metrics + logs export to OTLP</item>
    ///     </list>
    /// </summary>
    /// <remarks>
    ///     Signal-to-log routing is not wired by a bespoke bridge. Each atom /
    ///     coordinator / escalator writes via its own
    ///     <see cref="Microsoft.Extensions.Logging.ILogger"/> and operators
    ///     filter via the standard <c>Logging:LogLevel</c> config surface --
    ///     the same shape every other .NET component uses. A dedicated bridge
    ///     duplicates what the framework already provides.
    /// </remarks>
    public static IServiceCollection AddStyloBotObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<StyloBotObservabilityOptions>()
            .Bind(configuration.GetSection(StyloBotObservabilityOptions.SectionName));

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        var snapshot = configuration
            .GetSection(StyloBotObservabilityOptions.SectionName)
            .Get<StyloBotObservabilityOptions>() ?? new StyloBotObservabilityOptions();

        if (snapshot.PublishDetectionEventsToSerilog)
        {
            services.RemoveAll<IDetectionEventPublisher>();
            services.AddSingleton<IDetectionEventPublisher, SerilogDetectionEventPublisher>();
        }

        services.AddStyloBotOpenTelemetryCore(snapshot.OpenTelemetry);

        return services;
    }
}