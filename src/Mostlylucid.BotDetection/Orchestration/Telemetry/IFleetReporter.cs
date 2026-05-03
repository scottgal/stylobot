namespace Mostlylucid.BotDetection.Orchestration.Telemetry;

/// <summary>
///     Extension point for reporting detection events to an external fleet management system.
///     Commercial packages implement this to push telemetry to a central control plane via
///     HTTPS + Redis for cross-instance correlation and aggregated reporting.
///
///     FOSS ships with no implementations - detection events stay local to the gateway.
///     Multiple implementations can be registered; each gets a copy of every report.
/// </summary>
public interface IFleetReporter
{
    /// <summary>
    ///     Report a completed detection event. Implementations should buffer internally
    ///     and flush asynchronously - this call must not block the request pipeline.
    /// </summary>
    /// <param name="report">The detection event data</param>
    /// <param name="ct">Cancellation token (request scope, not reporter scope)</param>
    ValueTask ReportAsync(DetectionReport report, CancellationToken ct = default);

    /// <summary>Human-readable name for diagnostics/logging.</summary>
    string Name { get; }
}

/// <summary>
///     Single detection event for fleet-wide aggregation. This is a flattened, serializable
///     snapshot - not the in-memory AggregatedEvidence.
/// </summary>
public sealed record DetectionReport
{
    public required DateTime Timestamp { get; init; }
    public required string RequestId { get; init; }
    public required string Signature { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public int StatusCode { get; init; }
    public bool IsBot { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public string? RiskBand { get; init; }
    public string? Action { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? CountryCode { get; init; }
    public double ProcessingTimeMs { get; init; }

    /// <summary>
    ///     Detector contributions: detector name → confidence delta.
    ///     Used by commercial radar shape visualization and cross-instance correlation.
    /// </summary>
    public IReadOnlyDictionary<string, double>? DetectorContributions { get; init; }

    /// <summary>
    ///     Important signals (PII-safe hashes and categorical values only).
    ///     Used by commercial analytics dashboards.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ImportantSignals { get; init; }

    /// <summary>
    ///     Session vector if this request completed a session (retrogressive boundary).
    ///     Serialized as byte[] for transport efficiency. Commercial stores in pgvector.
    /// </summary>
    public byte[]? SessionVector { get; init; }

    /// <summary>Top reasons (human-readable narrative fragments)</summary>
    public IReadOnlyList<string>? TopReasons { get; init; }
}