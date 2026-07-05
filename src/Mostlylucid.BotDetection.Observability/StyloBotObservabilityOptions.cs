namespace Mostlylucid.BotDetection.Observability;

/// <summary>
///     Configuration root bound from <c>BotDetection:Observability</c>.
/// </summary>
/// <remarks>
///     Signal-to-log routing is not configured here -- it uses the standard
///     .NET <c>Logging:LogLevel</c> config surface, filtering by the atom /
///     escalator's own <see cref="Microsoft.Extensions.Logging.ILogger"/>
///     category. Operators tune per-atom / per-namespace via
///     <c>appsettings.json</c> <c>Logging</c> section, not a bespoke bridge.
/// </remarks>
public sealed class StyloBotObservabilityOptions
{
    public const string SectionName = "BotDetection:Observability";

    public bool PublishDetectionEventsToSerilog { get; set; } = true;

    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();

    public sealed class OpenTelemetryOptions
    {
        public bool EnableTracing { get; set; } = true;
        public bool EnableMetrics { get; set; } = true;
        public bool EnableLogs { get; set; } = true;

        /// <summary>OTLP endpoint. When null, OTel SDK default is used (http://localhost:4317).</summary>
        public string? OtlpEndpoint { get; set; }

        /// <summary>Service name on emitted resources. Defaults to "stylobot".</summary>
        public string ServiceName { get; set; } = "stylobot";

        /// <summary>Optional service.namespace resource attribute.</summary>
        public string? ServiceNamespace { get; set; }

        /// <summary>Optional service.instance.id resource attribute.</summary>
        public string? ServiceInstanceId { get; set; }
    }
}
