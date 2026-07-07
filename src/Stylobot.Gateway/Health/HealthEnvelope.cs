using System.Text.Json;

namespace Stylobot.Gateway.Health;

/// <summary>
/// Point-in-time result of one active health probe tick, serialized to
/// <see cref="Stylobot.Gateway.Data.DestinationEntity.Health"/> as camelCase JSON.
/// </summary>
/// <param name="Status">One of <c>"healthy"</c>, <c>"unhealthy"</c>, or <c>"unknown"</c>.</param>
/// <param name="LatencyMs">Round-trip latency of the probe in milliseconds.</param>
/// <param name="CheckedAtUtc">Wall-clock time the probe result was recorded.</param>
/// <param name="Reason">Short failure description; null when <paramref name="Status"/> is <c>"healthy"</c>.</param>
public record HealthEnvelope(string Status, int LatencyMs, DateTimeOffset CheckedAtUtc, string? Reason)
{
    /// <summary>
    /// Shared camelCase serializer options used when writing <see cref="HealthEnvelope"/>
    /// to <c>DestinationEntity.Health</c>.
    /// </summary>
    internal static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
