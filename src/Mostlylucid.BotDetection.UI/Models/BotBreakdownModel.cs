namespace Mostlylucid.BotDetection.UI.Models;

public sealed class BotBreakdownModel
{
    public required string BasePath { get; init; }
    public required IReadOnlyList<BotCategoryStats> Categories { get; init; }
    public int TotalBotSessions { get; init; }
}

public sealed class BotCategoryStats
{
    public required string Category { get; init; }
    public required string DisplayName { get; init; }
    public int SessionCount { get; init; }
    public double Percentage { get; set; }
    public IReadOnlyList<string> TopEndpoints { get; init; } = [];
    public string Color { get; init; } = "#ef4444";
}
