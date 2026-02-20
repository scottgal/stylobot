using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Thread-safe in-memory cache for computed dashboard aggregates.
///     Populated periodically by <see cref="DashboardSummaryBroadcaster" />.
///     API endpoints read from here instead of querying DB + enriching with in-memory stores.
/// </summary>
public sealed class DashboardAggregateCache
{
    private volatile AggregateSnapshot _snapshot = AggregateSnapshot.Empty;

    /// <summary>Current cached snapshot. Never null — returns empty defaults before first refresh.</summary>
    public AggregateSnapshot Current => _snapshot;

    /// <summary>Replace the entire snapshot atomically.</summary>
    public void Update(AggregateSnapshot snapshot) => _snapshot = snapshot;

    public sealed record AggregateSnapshot
    {
        public required List<DashboardCountryStats> Countries { get; init; }
        public required List<DashboardUserAgentSummary> UserAgents { get; init; }
        public DateTime ComputedAt { get; init; } = DateTime.UtcNow;

        public static AggregateSnapshot Empty => new()
        {
            Countries = [],
            UserAgents = [],
            ComputedAt = DateTime.MinValue
        };
    }
}
