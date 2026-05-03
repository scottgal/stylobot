namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Detailed statistics for a single country, used by the /api/countries/{code} drill-down endpoint.
/// </summary>
public sealed record DashboardCountryDetail
{
    public required string CountryCode { get; init; }
    public string? CountryName { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount => TotalCount - BotCount;
    public double BotRate { get; init; }
    public Dictionary<string, int> TopBotTypes { get; init; } = new();
    public Dictionary<string, int> TopActions { get; init; } = new();
    public List<DashboardTopBotEntry> TopBots { get; init; } = new();
}
