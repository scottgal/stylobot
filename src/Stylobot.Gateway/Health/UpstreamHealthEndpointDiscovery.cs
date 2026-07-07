using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Data;

namespace Stylobot.Gateway.Health;

/// <summary>
/// Probes YARP upstream clusters for health endpoints, caches the winner per cluster,
/// and persists it into <c>ClusterEntity.MetadataJson["healthEndpoint"]</c> so a
/// process restart does not need to re-probe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache semantics (per-process):</b>
/// <list type="bullet">
/// <item><c>_cache</c>: hot path. Populated by <see cref="DiscoverAsync"/> and (lazily) by
/// <see cref="GetCached"/> on the first miss. Cleared by <see cref="Invalidate"/>.</item>
/// <item><c>_warmedFromPersistence</c>: used as a set. Set on the first <see cref="GetCached"/>
/// miss and on every successful <see cref="DiscoverAsync"/>. Once set, persistence is not
/// re-read for that cluster this process, even after <see cref="Invalidate"/>. This ensures
/// the next tick re-probes fresh rather than re-loading the now-stale persisted path.</item>
/// </list>
/// </para>
/// <para>
/// <b>DB access:</b> uses <see cref="IServiceScopeFactory"/> per operation to avoid a
/// captive scoped <see cref="GatewayDbContext"/> inside a singleton. Each operation opens
/// one scope, executes, then disposes.
/// </para>
/// </remarks>
public sealed class UpstreamHealthEndpointDiscovery : IUpstreamHealthEndpointDiscovery
{
    private const string MetadataKey = "healthEndpoint";

    private readonly HttpClient _httpClient;
    private readonly UpstreamHealthMonitorOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpstreamHealthEndpointDiscovery> _logger;

    private readonly ConcurrentDictionary<string, DiscoveredHealthEndpoint> _cache = new();

    // Set keeps track of which clusters have been warmed from persistence this process
    // (whether the result was null or not). Prevents re-reading after Invalidate.
    private readonly ConcurrentDictionary<string, byte> _warmedFromPersistence = new();

    public UpstreamHealthEndpointDiscovery(
        HttpClient httpClient,
        IOptions<UpstreamHealthMonitorOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<UpstreamHealthEndpointDiscovery> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<DiscoveredHealthEndpoint?> DiscoverAsync(
        string clusterId, string destinationAddress, CancellationToken ct)
    {
        foreach (var path in _options.CandidatePaths)
        {
            var url = $"{destinationAddress.TrimEnd('/')}{path}";
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromMilliseconds(_options.ProbeTimeoutMs));

                var response = await _httpClient.GetAsync(url, probeCts.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                    continue;

                var contentType = response.Content.Headers.ContentType?.ToString();
                var endpoint = new DiscoveredHealthEndpoint(path, contentType, DateTimeOffset.UtcNow);

                _cache[clusterId] = endpoint;
                _warmedFromPersistence[clusterId] = 0;
                await PersistAsync(clusterId, endpoint, ct);

                _logger.LogInformation(
                    "Discovered health endpoint for cluster {ClusterId}: {Path}", clusterId, path);
                return endpoint;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Per-probe timeout or transient error: skip this candidate.
                _logger.LogDebug(ex, "Probe failed for {Url} — trying next candidate", url);
            }
        }

        _logger.LogDebug("No health endpoint found for cluster {ClusterId} — all candidates exhausted", clusterId);
        return null;
    }

    /// <inheritdoc/>
    public DiscoveredHealthEndpoint? GetCached(string clusterId)
    {
        if (_cache.TryGetValue(clusterId, out var hit))
            return hit;

        // Guard: if already warmed (including after Invalidate), do not re-read DB.
        if (_warmedFromPersistence.ContainsKey(clusterId))
            return null;

        // First miss for this cluster this process: try to warm from persistence.
        _warmedFromPersistence[clusterId] = 0;
        var persisted = ReadPersisted(clusterId);
        if (persisted is not null)
            _cache[clusterId] = persisted;

        return persisted;
    }

    /// <inheritdoc/>
    public void Invalidate(string clusterId)
    {
        _cache.TryRemove(clusterId, out _);
        // _warmedFromPersistence is intentionally NOT cleared: the next GetCached on
        // this instance must not re-warm from the stale persisted value; it must wait
        // for the next DiscoverAsync tick to produce a fresh endpoint.
    }

    // ── persistence helpers ──────────────────────────────────────────────────

    private async Task PersistAsync(string clusterId, DiscoveredHealthEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            var cluster = await db.Clusters.FindAsync(new object[] { clusterId }, ct);
            if (cluster is null)
            {
                cluster = new ClusterEntity { ClusterId = clusterId };
                db.Clusters.Add(cluster);
            }

            cluster.MetadataJson = MergeHealthEndpoint(cluster.MetadataJson, endpoint);
            cluster.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist health endpoint for cluster {ClusterId}", clusterId);
        }
    }

    private DiscoveredHealthEndpoint? ReadPersisted(string clusterId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            var cluster = db.Clusters.Find(clusterId);
            if (cluster?.MetadataJson is null)
                return null;

            return ParseHealthEndpoint(cluster.MetadataJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read persisted health endpoint for cluster {ClusterId}", clusterId);
            return null;
        }
    }

    // ── JSON merge / parse ───────────────────────────────────────────────────

    private static string MergeHealthEndpoint(string? existingJson, DiscoveredHealthEndpoint endpoint)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            // Malformed JSON in column: start fresh rather than propagating.
            root = new JsonObject();
        }

        root[MetadataKey] = new JsonObject
        {
            ["path"] = endpoint.Path,
            ["contentType"] = endpoint.ContentType,
            ["discoveredAtUtc"] = endpoint.DiscoveredAtUtc.ToString("O"),
        };

        return root.ToJsonString();
    }

    private static DiscoveredHealthEndpoint? ParseHealthEndpoint(string metadataJson)
    {
        try
        {
            var root = JsonNode.Parse(metadataJson)?.AsObject();
            if (root is null) return null;

            var he = root[MetadataKey]?.AsObject();
            if (he is null) return null;

            var path = he["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path)) return null;

            var contentType = he["contentType"]?.GetValue<string>();
            var discoveredAtUtcStr = he["discoveredAtUtc"]?.GetValue<string>();
            var discoveredAtUtc = DateTimeOffset.TryParse(discoveredAtUtcStr, out var dt)
                ? dt
                : DateTimeOffset.UtcNow;

            return new DiscoveredHealthEndpoint(path, contentType, discoveredAtUtc);
        }
        catch
        {
            return null;
        }
    }
}
