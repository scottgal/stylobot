namespace Mostlylucid.BotDetection.Orchestration.Telemetry;

/// <summary>
///     Extension point for publishing per-request detection events in real time to an
///     out-of-process consumer - typically a Redis pub/sub channel consumed by a separate
///     Stylobot-UI container. Distinct from <see cref="IFleetReporter"/> which is batched
///     and control-plane-destined:
///
///     <list type="bullet">
///       <item><description><b>IDetectionEventPublisher</b> (this): per-request, ephemeral, fire-and-forget, UI-facing.</description></item>
///       <item><description><b>IFleetReporter</b>: batched, persistent, control-plane-facing, eventually-consistent.</description></item>
///     </list>
///
///     FOSS ships <see cref="NullDetectionEventPublisher"/> as the default - no-op, zero
///     cost. Commercial installs wire the <c>RedisDetectionEventPublisher</c> to fan events
///     out to the cluster-wide channel so a standalone UI container can render them
///     without being in the request path.
/// </summary>
public interface IDetectionEventPublisher
{
    /// <summary>
    ///     Publish a completed detection. MUST NOT block the request pipeline - implementations
    ///     should buffer internally or use fire-and-forget pub/sub.
    /// </summary>
    ValueTask PublishAsync(DetectionEvent evt, CancellationToken ct = default);

    /// <summary>Human-readable name for diagnostics.</summary>
    string Name { get; }
}

/// <summary>
///     Per-request detection event for real-time UI streaming. Strictly smaller than
///     <see cref="DetectionReport"/> - only fields a live dashboard actually renders.
///     No raw PII - the UI side is expected to render this directly.
/// </summary>
public sealed record DetectionEvent
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
    public string? ThreatBand { get; init; }
    public string? Action { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? CountryCode { get; init; }
    public double ProcessingTimeMs { get; init; }

    /// <summary>Detector → confidence-delta map for the dashboard's contribution breakdown.</summary>
    public IReadOnlyDictionary<string, double>? DetectorContributions { get; init; }

    /// <summary>Human-readable narrative fragments for the sparkline tooltip.</summary>
    public IReadOnlyList<string>? TopReasons { get; init; }

    /// <summary>Which gateway saw this request - for multi-gateway dashboards.</summary>
    public string? GatewayId { get; init; }
}

/// <summary>
///     Default no-op publisher. Registered unconditionally by the FOSS DI so that middleware
///     can resolve the dependency without caring whether a real publisher is wired up.
/// </summary>
public sealed class NullDetectionEventPublisher : IDetectionEventPublisher
{
    public string Name => "null";
    public ValueTask PublishAsync(DetectionEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;
}