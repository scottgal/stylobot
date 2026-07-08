namespace Stylobot.Gateway.Health;

/// <summary>
/// A health endpoint discovered by probing a YARP upstream destination.
/// </summary>
/// <param name="Path">The candidate path that returned HTTP 200, e.g. <c>/healthz</c>.</param>
/// <param name="ContentType">The <c>Content-Type</c> header from the successful probe response, if present.</param>
/// <param name="DiscoveredAtUtc">UTC timestamp when the probe succeeded.</param>
public record DiscoveredHealthEndpoint(string Path, string? ContentType, DateTimeOffset DiscoveredAtUtc);

/// <summary>
/// Probes YARP upstream destinations to discover health endpoints, caches winners per cluster,
/// and persists them so a process restart does not re-probe.
/// </summary>
public interface IUpstreamHealthEndpointDiscovery
{
    /// <summary>
    /// Probes each candidate path on <paramref name="destinationAddress"/> in order.
    /// The first path that returns HTTP 200 is the winner. All other status codes and
    /// probe-level exceptions are treated as "not a health endpoint" and probing continues.
    /// Returns <see langword="null"/> when all candidates fail.
    /// On success, caches the result in-process and persists it into
    /// <c>ClusterEntity.MetadataJson</c> under the <c>healthEndpoint</c> key (merge, not clobber).
    /// </summary>
    Task<DiscoveredHealthEndpoint?> DiscoverAsync(string clusterId, string destinationAddress, CancellationToken ct);

    /// <summary>
    /// Returns the cached endpoint for <paramref name="clusterId"/>.
    /// On the first miss per process, warms the cache from the persisted
    /// <c>ClusterEntity.MetadataJson</c> value. After <see cref="Invalidate"/> is called,
    /// the warm-from-persistence guard is already set, so the persisted value is not re-read
    /// on this process instance.
    /// </summary>
    DiscoveredHealthEndpoint? GetCached(string clusterId);

    /// <summary>
    /// Removes <paramref name="clusterId"/> from the in-process hot cache so the next
    /// tick re-discovers via fresh probing. Does not touch persistence.
    /// </summary>
    void Invalidate(string clusterId);
}
