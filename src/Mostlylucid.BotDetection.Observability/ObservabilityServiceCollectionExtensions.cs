using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Observability.OpenTelemetry;
using Mostlylucid.BotDetection.Observability.Signals;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    ///     Wires the StyloBot observability stack:
    ///     <list type="bullet">
    ///         <item>SerilogDetectionEventPublisher replaces the no-op IDetectionEventPublisher</item>
    ///         <item>BlackboardSignalLogBridge forwards global signals to ILogger</item>
    ///         <item>OpenTelemetry tracing + metrics + logs export to OTLP</item>
    ///     </list>
    /// </summary>
    public static IServiceCollection AddStyloBotObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<StyloBotObservabilityOptions>()
            .Bind(configuration.GetSection(StyloBotObservabilityOptions.SectionName));

        services.AddOptions<BlackboardSignalLogOptions>()
            .Configure<IOptions<StyloBotObservabilityOptions>>((target, src) =>
            {
                target.Enabled = src.Value.SignalLog.Enabled;
                target.IncludePrefixes = src.Value.SignalLog.IncludePrefixes;
                target.ExcludePrefixes = src.Value.SignalLog.ExcludePrefixes;
            });

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        var snapshot = configuration
            .GetSection(StyloBotObservabilityOptions.SectionName)
            .Get<StyloBotObservabilityOptions>() ?? new StyloBotObservabilityOptions();

        if (snapshot.PublishDetectionEventsToSerilog)
        {
            services.RemoveAll<IDetectionEventPublisher>();
            services.AddSingleton<IDetectionEventPublisher, SerilogDetectionEventPublisher>();
        }

        services.AddHostedService<BlackboardSignalLogBridge>();

        services.AddStyloBotOpenTelemetryCore(snapshot.OpenTelemetry);

        return services;
    }
}
