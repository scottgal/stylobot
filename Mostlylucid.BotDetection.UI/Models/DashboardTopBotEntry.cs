namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top bot entry from the event store, used by the /api/topbots endpoint.
/// </summary>
public sealed record DashboardTopBotEntry
{
    public required string PrimarySignature { get; init; }
    public int HitCount { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? RiskBand { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public string? Action { get; init; }
    public string? CountryCode { get; init; }
    public double ProcessingTimeMs { get; init; }
    public List<string>? TopReasons { get; init; }
    public DateTime LastSeen { get; init; }
    public string? Narrative { get; init; }
    public string? Description { get; init; }
    public bool IsKnownBot { get; init; }
}
