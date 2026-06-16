using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     A single entry from the arcjet well-known-bots catalog
///     (https://github.com/arcjet/well-known-bots).
/// </summary>
public sealed class WellKnownBotEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("categories")]
    public string[] Categories { get; init; } = [];

    [JsonPropertyName("pattern")]
    public WellKnownBotPattern Pattern { get; init; } = new();

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class WellKnownBotPattern
{
    [JsonPropertyName("accepted")]
    public string[] Accepted { get; init; } = [];

    [JsonPropertyName("forbidden")]
    public string[] Forbidden { get; init; } = [];
}