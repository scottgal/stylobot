namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Endpoint-level statistics aggregated by method + path.
/// </summary>
public sealed record DashboardEndpointStats
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount => TotalCount - BotCount;
    public double BotRate { get; init; }
    public long BytesOut { get; init; }
    public int UniqueSignatures { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double MinProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    public double P95ProcessingTimeMs { get; init; }
    public double AvgThreatScore { get; init; }
    public string? TopAction { get; init; }
    public string? DominantRiskBand { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>
    ///     The detection policy that resolves for this endpoint's path.
    ///     Populated from IPolicyRegistry.GetPolicyForPath() when available.
    /// </summary>
    public string? ActivePolicyName { get; init; }

    public bool IsPinned { get; init; }
    public bool IsHoneypot { get; init; }
    public long? PinId { get; init; }

    /// <summary>
    ///     HTTP response status bucket counts populated by the endpoint
    ///     aggregator (SQLite + Postgres backends both group on these). Used
    ///     by SbEndpointsList to render the status mix column and to drive
    ///     the status-code filter chips. Buckets are inclusive of both
    ///     boundaries (2xx = 200..299 etc.); status_code = 0 (default in
    ///     dashboard_events when the response phase was never observed)
    ///     contributes to none of the buckets.
    /// </summary>
    public int Status2xx { get; init; }
    public int Status3xx { get; init; }
    public int Status4xx { get; init; }
    public int Status5xx { get; init; }

    /// <summary>
    ///     The dominant status bucket for filter / sort purposes: the bucket
    ///     with the most hits, or empty when no status was ever observed.
    ///     Mirrors the operator workflow ("which endpoints are mostly 5xx
    ///     right now?") without forcing callers to recompute the argmax.
    /// </summary>
    public string DominantStatusBucket
    {
        get
        {
            var max = Status2xx;
            var bucket = Status2xx > 0 ? "2xx" : "";
            if (Status3xx > max) { max = Status3xx; bucket = "3xx"; }
            if (Status4xx > max) { max = Status4xx; bucket = "4xx"; }
            if (Status5xx > max) { max = Status5xx; bucket = "5xx"; }
            return bucket;
        }
    }

    /// <summary>
    ///     Real-origin status bucket counts (UPSTREAM column), same shape and same
    ///     inclusive-boundary convention as <see cref="Status2xx"/>/3xx/4xx/5xx above,
    ///     because one path can mix forwarded and blocked/honeypot/throttled traffic.
    ///     Sourced from <c>detections.upstream_status_code</c> (stamped by the gateway's
    ///     <c>UpstreamStatusTransform</c> only for requests that actually reached
    ///     <c>MapReverseProxy</c>).
    /// </summary>
    public int UpstreamStatus2xx { get; init; }
    public int UpstreamStatus3xx { get; init; }
    public int UpstreamStatus4xx { get; init; }
    public int UpstreamStatus5xx { get; init; }

    /// <summary>
    ///     Count of rows with no real origin call at all (honeypot / blocked / throttled --
    ///     resolved before <c>MapReverseProxy</c> ever ran). Deliberately separate from the
    ///     status buckets above rather than folded into one fake "the" upstream status per
    ///     row: for these rows, "no upstream status" is itself the correct, meaningful story,
    ///     not missing data.
    /// </summary>
    public int UpstreamNoneCount { get; init; }
}

/// <summary>
///     Behavioral profile for a group of requests (bots, humans, or all) on a single endpoint.
///     All values are percentages (0-100) ready for radar chart rendering.
/// </summary>
public sealed record EndpointBehavioralProfile
{
    public double ThreatScore { get; init; }      // avg threat_score normalized to 0-100
    public double Probability { get; init; }       // avg bot_probability * 100
    public double Confidence { get; init; }        // avg confidence * 100
    public double BlockRate { get; init; }         // fraction blocked * 100
    public double ErrorRate { get; init; }         // fraction 4xx/5xx * 100
    public double LatencyScore { get; init; }      // latency relative to endpoint max, 0-100
    public int SampleCount { get; init; }
}

/// <summary>
///     Detailed drill-down for a single endpoint.
/// </summary>
public sealed record DashboardEndpointDetail
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int TotalCount { get; init; }
    public int BotCount { get; init; }
    public int HumanCount => TotalCount - BotCount;
    public double BotRate { get; init; }
    public int UniqueSignatures { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double MinProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    public double P95ProcessingTimeMs { get; init; }
    public double AvgThreatScore { get; init; }
    public required Dictionary<string, int> TopActions { get; init; }
    public required Dictionary<string, int> TopCountries { get; init; }
    public required Dictionary<string, int> RiskBands { get; init; }
    public required List<DashboardTopBotEntry> TopBots { get; init; }
    public required List<SignatureDetectionRow> RecentDetections { get; init; }
    public EndpointBehavioralProfile? BotProfile { get; init; }
    public EndpointBehavioralProfile? HumanProfile { get; init; }
    public EndpointBehavioralProfile? OverallProfile { get; init; }
}

public sealed record EndpointPackCoverage(
    string PackName,
    string Scope,
    int CurrentLevel,
    string? CurrentPolicy);

/// <summary>
///     Per-endpoint stats scoped to a single signature, surfaced on the signature
///     detail page's Endpoints Visited table. Mirrors the shape of
///     <see cref="DashboardEndpointStats"/> with the additions of the response
///     status mix and a "dominant" action so the operator can read at a glance
///     whether this signature gets allowed / throttled / blocked on each path.
///
///     <para>
///     P95 is approximated as <c>avg + 0.9 * (max - avg)</c> on SQLite (same
///     convention as every other p95 column in the dashboard); the Postgres
///     mirror returns the real percentile.
///     </para>
/// </summary>
public sealed record SignatureEndpointStats
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int HitCount { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public double MaxProcessingTimeMs { get; init; }
    public double P95ProcessingTimeMs { get; init; }
    public double BotRate { get; init; }            // 0..1, fraction of hits that scored as bot
    public double AvgThreatScore { get; init; }     // 0..1
    public string? DominantRiskBand { get; init; }
    public string? DominantAction { get; init; }
    public int Status2xx { get; init; }
    public int Status4xx { get; init; }
    public int Status5xx { get; init; }
    public DateTime LastSeen { get; init; }
}
