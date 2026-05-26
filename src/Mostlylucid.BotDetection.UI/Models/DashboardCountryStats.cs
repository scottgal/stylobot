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
    /// <summary>Total bytes sent in responses for requests from this country.</summary>
    public long BytesOut { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    /// <summary>
    ///     P95 processing-time approximation: avg + 90% of (max - avg).
    ///     SQLite lacks PERCENTILE_CONT; the Postgres mirror in Task 10 replaces this with native p95.
    /// </summary>
    public double P95ProcessingTimeMs { get; init; }
}
