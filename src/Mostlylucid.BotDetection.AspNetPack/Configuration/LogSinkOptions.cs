using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Configuration;

public sealed class LogSinkOptions
{
    public bool Enabled { get; set; } = true;
    public string GatewayEndpoint { get; set; } = "http://stylobot-gateway:4318";
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
    public int BatchSize { get; set; } = 100;
    public int QueueCapacity { get; set; } = 5_000;
    public string FlushTick { get; set; } = "tick.10s";
    public string[] AllowedCategories { get; set; } = new[] { "*" };
    public TimeSpan AlertAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Hard ceiling on a single OTLP export POST. Telemetry is best-effort and
    ///     lives on a background drainer, so an unreachable/slow collector must fail
    ///     fast and NEVER hang the drainer (a jammed drainer starves the tick
    ///     coordinator and, on a busy host, couples into request latency). Without
    ///     this the export inherits the HttpClient default (100s) and an unresolvable
    ///     endpoint blocks on the DNS timeout (~14-21s) per attempt. Default 5s.
    /// </summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
