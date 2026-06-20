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
}
