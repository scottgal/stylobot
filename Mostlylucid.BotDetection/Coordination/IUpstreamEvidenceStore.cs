using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Coordination;

/// <summary>
/// Abstraction for storing/retrieving detection evidence between gateway and downstream services.
/// The default implementation is a no-op (headers carry the data).
/// Redis, SQL, or other backends can replace this to avoid header-size limits.
/// </summary>
public interface IUpstreamEvidenceStore
{
    /// <summary>
    /// Store aggregated evidence keyed by request ID. Called by the gateway after detection.
    /// </summary>
    /// <param name="requestId">Unique request identifier (typically HttpContext.TraceIdentifier)</param>
    /// <param name="evidence">The full detection evidence</param>
    /// <param name="ct">Cancellation token</param>
    Task StoreAsync(string requestId, AggregatedEvidence evidence, CancellationToken ct = default);

    /// <summary>
    /// Retrieve aggregated evidence by request ID. Called by downstream middleware.
    /// Returns null if not found or if the store is a no-op.
    /// </summary>
    /// <param name="requestId">Unique request identifier</param>
    /// <param name="ct">Cancellation token</param>
    Task<AggregatedEvidence?> RetrieveAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// Whether this store is active (not a no-op).
    /// When true, the gateway can skip heavy header serialization and just pass the request ID.
    /// </summary>
    bool IsActive { get; }
}
