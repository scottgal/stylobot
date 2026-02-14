namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Historical reputation data from TimescaleDB for a specific signature.
/// </summary>
public sealed record TimescaleReputationData
{
    public required double BotRatio { get; init; }
    public required int TotalHitCount { get; init; }
    public required int DaysActive { get; init; }
    public required int RecentHourHitCount { get; init; }
    public required double AverageBotProbability { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>
    ///     Whether the historical data is conclusive enough to skip LLM analysis.
    ///     True when bot ratio is strongly one way AND there's enough support.
    /// </summary>
    public bool IsConclusive => TotalHitCount >= 3 && (BotRatio >= 0.8 || BotRatio <= 0.2);
}
