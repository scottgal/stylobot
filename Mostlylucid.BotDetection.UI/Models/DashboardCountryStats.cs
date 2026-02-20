namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Country-level statistics from the event store, used by the /api/countries endpoint.
/// </summary>
public sealed record DashboardCountryStats
{
    public required string CountryCode { get; init; }
    public string? CountryName { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount => TotalCount - BotCount;
    public double BotRate { get; init; }
}
