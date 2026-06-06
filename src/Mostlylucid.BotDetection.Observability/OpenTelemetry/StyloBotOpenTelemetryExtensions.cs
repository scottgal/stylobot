using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Telemetry;
using global::OpenTelemetry.Logs;
using global::OpenTelemetry.Metrics;
using global::OpenTelemetry.Resources;
using global::OpenTelemetry.Trace;

namespace Mostlylucid.BotDetection.Observability.OpenTelemetry;

internal static class StyloBotOpenTelemetryExtensions
{
    public static IServiceCollection AddStyloBotOpenTelemetryCore(
        this IServiceCollection services,
        StyloBotObservabilityOptions.OpenTelemetryOptions otel)
    {
        void ConfigureResource(ResourceBuilder rb) => rb
            .AddService(
                serviceName: otel.ServiceName,
                serviceNamespace: otel.ServiceNamespace,
                serviceInstanceId: otel.ServiceInstanceId);

        var builder = services.AddOpenTelemetry().ConfigureResource(ConfigureResource);

        if (otel.EnableTracing)
        {
            builder.WithTracing(t =>
            {
                t.AddSource(BotDetectionTelemetry.ActivitySourceName);
                t.AddAspNetCoreInstrumentation();
                t.AddOtlpExporter(o => ApplyEndpoint(o, otel.OtlpEndpoint));
            });
        }

        if (otel.EnableMetrics)
        {
            builder.WithMetrics(m =>
            {
                m.AddMeter(BotDetectionMetrics.MeterName);
                m.AddMeter(BotDetectionSignalMeter.MeterName);
                m.AddAspNetCoreInstrumentation();
                m.AddOtlpExporter(o => ApplyEndpoint(o, otel.OtlpEndpoint));
            });
        }

        if (otel.EnableLogs)
        {
            var logsResource = ResourceBuilder.CreateDefault();
            ConfigureResource(logsResource);

            services.AddLogging(lb =>
            {
                lb.AddOpenTelemetry(o =>
                {
                    o.SetResourceBuilder(logsResource);
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = true;
                    o.AddOtlpExporter(opts => ApplyEndpoint(opts, otel.OtlpEndpoint));
                });
            });
        }

        return services;
    }

    private static void ApplyEndpoint(
        global::OpenTelemetry.Exporter.OtlpExporterOptions o,
        string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
            o.Endpoint = new Uri(endpoint);
    }
}
