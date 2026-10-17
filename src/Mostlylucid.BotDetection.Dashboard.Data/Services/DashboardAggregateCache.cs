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
    private long _lastHitTicksUtc;

    /// <summary>Current cached snapshot. Never null - returns empty defaults before first refresh.</summary>
    public AggregateSnapshot Current => _snapshot;

    /// <summary>Replace the entire snapshot atomically.</summary>
    public void Update(AggregateSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>
    ///     Stamped by every <c>/api/v1</c> read endpoint each time it serves
    ///     a request (cache hit or store fall-through). Drives the
    ///     idle-skip heuristic in <see cref="DashboardSummaryBroadcaster"/>:
    ///     when no one has actually consumed the dashboard surface for a
    ///     while, the broadcaster stops paying the precompute cost until
    ///     the next request arrives. Stored as ticks for cheap atomic
    ///     reads/writes via <see cref="Interlocked"/>.
    /// </summary>
    public void MarkHit() => Interlocked.Exchange(ref _lastHitTicksUtc, DateTime.UtcNow.Ticks);

    /// <summary>
    ///     UTC time of the most recent endpoint request, or
    ///     <see cref="DateTime.MinValue"/> when nothing has ever read.
    /// </summary>
    public DateTime LastHitAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastHitTicksUtc);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public sealed record AggregateSnapshot
    {
        public required List<DashboardCountryStats> Countries { get; init; }
        public required List<DashboardEndpointStats> Endpoints { get; init; }
        public required List<DashboardUserAgentSummary> UserAgents { get; init; }

        /// <summary>
        ///     Latest summary numbers (totals, bot/human counts, top types,
        ///     processing-time stats). Populated each broadcaster tick so
        ///     <c>HandleSummary</c> can return without touching the store.
        /// </summary>
        public DashboardSummary? Summary { get; init; }

        /// <summary>
        ///     Default-view recent detection rows. Sized to the largest
        ///     limit the dashboard's recent-activity panels ask for so the
        ///     no-filter path serves entirely from cache; filtered or
        ///     beyond-window queries still fall through to the store.
        /// </summary>
        public List<DashboardDetectionEvent> Detections { get; init; } = [];

        /// <summary>
        ///     Default-view time-series (trailing 24 h, 5-minute buckets).
        ///     Endpoint callers asking for the same window read from here;
        ///     custom intervals or different windows fall through.
        /// </summary>
        public List<DashboardTimeSeriesPoint> TimeSeries { get; init; } = [];

        /// <summary>
        ///     Default-view top-bots ranking (latest 100, no time window).
        ///     Other rankings or windowed queries fall through to the
        ///     store; this snapshot covers the home-dashboard render.
        /// </summary>
        public List<DashboardTopBotEntry> TopBots { get; init; } = [];

        /// <summary>
        ///     Default-view threat ranking (latest 20, no time window).
        ///     Other rankings fall through.
        /// </summary>
        public List<ThreatEntry> Threats { get; init; } = [];

        public DateTime ComputedAt { get; init; } = DateTime.UtcNow;

        public static readonly AggregateSnapshot Empty = new()
        {
            Countries = [],
            Endpoints = [],
            UserAgents = [],
            Summary = null,
            Detections = [],
            TimeSeries = [],
            TopBots = [],
            Threats = [],
            ComputedAt = DateTime.MinValue
        };
    }
}