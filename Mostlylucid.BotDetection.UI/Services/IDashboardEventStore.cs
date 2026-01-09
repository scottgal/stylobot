using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Storage for dashboard events (detections and signatures).
/// </summary>
public interface IDashboardEventStore
{
    /// <summary>
    ///     Add a detection event to the store.
    /// </summary>
    Task AddDetectionAsync(DashboardDetectionEvent detection);

    /// <summary>
    ///     Add or update a signature observation in the store.
    ///     Returns the signature with updated hit_count after upsert.
    /// </summary>
    Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature);

    /// <summary>
    ///     Get recent detections with optional filtering.
    /// </summary>
    Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null);

    /// <summary>
    ///     Get recent signatures.
    /// </summary>
    Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100);

    /// <summary>
    ///     Get summary statistics.
    /// </summary>
    Task<DashboardSummary> GetSummaryAsync();

    /// <summary>
    ///     Get time-series data for charts.
    /// </summary>
    Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize);
}