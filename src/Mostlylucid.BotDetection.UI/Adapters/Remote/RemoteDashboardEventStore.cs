using Microsoft.Extensions.Caching.Memory;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over the gateway's core dashboard REST surface (the endpoints in
///     <c>Mostlylucid.BotDetection.Api/Endpoints/ReadEndpoints.cs</c>): detections,
///     signatures, summary, timeseries, country / endpoint / topbot / threat aggregates,
///     UA search, investigation. Writes (AddDetection / AddSignature / bot-name updates /
///     pruning) throw because the gateway owns those.
///
///     The dashboard middleware and view components consume <c>IDashboardEventStore</c>
///     directly; substituting this impl gives the remote viewer every JSON endpoint and
///     every Razor partial that hangs off it - which is most of the dashboard.
///
///     <para>
///     Every read path is wrapped in a short-TTL in-memory cache. A dashboard
///     render fans 8-10 SSR partials + the background broadcaster + lazy HTMX
///     loads at the gateway nearly simultaneously, and many request the same
///     endpoint with the same parameters within a few hundred milliseconds.
///     The cache key is the encoded query path, the TTL is 2 seconds, and the
///     value is the deserialised payload -- so the second through Nth duplicate
///     within a render burst is a single dictionary lookup instead of another
///     ~2 s gateway call. Writes (none here) and per-request data freshness
///     beyond the 2 s window are unaffected.
///     </para>
/// </summary>
internal sealed class RemoteDashboardEventStore : IDashboardEventStore
{
    /// <summary>
    ///     Short-TTL dedupe window for the duplicated SSR + broadcaster +
    ///     lazy-load fan-out a single dashboard render produces. Long enough
    ///     to catch the burst (typically &lt; 500 ms), short enough that the
    ///     next render still sees fresh data.
    /// </summary>
    private static readonly TimeSpan RenderBurstDedupeTtl = TimeSpan.FromSeconds(2);

    private readonly GatewayApiClient _api;
    private readonly IMemoryCache _cache;

    public RemoteDashboardEventStore(GatewayApiClient api, IMemoryCache cache)
    {
        _api = api;
        _cache = cache;
    }

    /// <summary>
    ///     Cache wrapper around the gateway-call delegate. Keys are scoped to
    ///     the remote-event-store namespace so they cannot collide with any
    ///     other consumer of the shared IMemoryCache.
    /// </summary>
    private Task<T> GetOrFetchAsync<T>(string cacheKey, Func<Task<T>> fetch)
    {
        var scopedKey = "rdes:" + cacheKey;
        if (_cache.TryGetValue<T>(scopedKey, out var cached) && cached is not null)
            return Task.FromResult(cached);

        return FetchAndCacheAsync();

        async Task<T> FetchAndCacheAsync()
        {
            var value = await fetch().ConfigureAwait(false);
            if (value is not null)
                _cache.Set(scopedKey, value, RenderBurstDedupeTtl);
            return value!;
        }
    }

    public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
    {
        var f = filter ?? new DashboardFilter();
        var query = $"/api/v1/detections?limit={f.Limit}&offset={f.Offset}"
            + (f.IsBot.HasValue ? $"&isBot={f.IsBot.Value.ToString().ToLowerInvariant()}" : "")
            + (f.StartTime.HasValue ? $"&since={Uri.EscapeDataString(f.StartTime.Value.ToString("o"))}" : "")
            + (!string.IsNullOrEmpty(f.SignatureId) ? $"&signature={Uri.EscapeDataString(f.SignatureId)}" : "");
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<DashboardDetectionEvent>>(query, ct);
            return list ?? new List<DashboardDetectionEvent>();
        });
    }

    public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
    {
        var query = $"/api/v1/signatures?limit={limit}&offset={offset}"
            + (isBot.HasValue ? $"&isBot={isBot.Value.ToString().ToLowerInvariant()}" : "");
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<DashboardSignatureEvent>>(query);
            return list ?? new List<DashboardSignatureEvent>();
        });
    }

    public Task<DashboardSummary> GetSummaryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null)
    {
        var query = "/api/v1/summary";
        var sep = '?';
        if (startTime.HasValue)
        {
            query += $"{sep}since={Uri.EscapeDataString(startTime.Value.ToString("o"))}";
            sep = '&';
        }
        if (endTime.HasValue)
        {
            query += $"{sep}until={Uri.EscapeDataString(endTime.Value.ToString("o"))}";
            sep = '&';
        }
        if (!string.IsNullOrEmpty(audienceFilter))
        {
            query += $"{sep}audience={Uri.EscapeDataString(audienceFilter)}";
            sep = '&';
        }
        query += AppendDomainQuery(domains, ref sep);

        return GetOrFetchAsync(query, async () =>
        {
            var summary = await _api.GetEnvelopeAsync<DashboardSummary>(query);
            return summary ?? EmptySummary();
        });
    }

    /// <summary>
    ///     Emit <c>?domain=X&amp;domain=Y</c> repeated params for the multi-select
    ///     domain filter. The gateway-side handler (or the traffic-page controller
    ///     when the ring flips) reads <c>Query["domain"]</c> as a StringValues
    ///     list, matching the FOSS convention (see <c>TrafficController</c>).
    /// </summary>
    private static string AppendDomainQuery(IReadOnlyList<string>? domains, ref char sep)
    {
        if (domains is null || domains.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var d in domains)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            sb.Append(sep).Append("domain=").Append(Uri.EscapeDataString(d));
            sep = '&';
        }
        return sb.ToString();
    }

    private static DashboardSummary EmptySummary() => new()
    {
        Timestamp = DateTime.UtcNow,
        TotalRequests = 0,
        BotRequests = 0,
        HumanRequests = 0,
        UncertainRequests = 0,
        RiskBandCounts = new(),
        TopBotTypes = new(),
        TopActions = new(),
        UniqueSignatures = 0
    };

    public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null)
    {
        var interval = bucketSize.TotalMinutes switch
        {
            <= 1.0 => "1m",
            <= 5.0 => "5m",
            <= 15.0 => "15m",
            _ => "1h"
        };
        var query = $"/api/v1/timeseries?interval={interval}"
            + $"&since={Uri.EscapeDataString(startTime.ToString("o"))}"
            + $"&until={Uri.EscapeDataString(endTime.ToString("o"))}";
        if (!string.IsNullOrEmpty(audienceFilter))
            query += $"&audience={Uri.EscapeDataString(audienceFilter)}";
        var sep = '&';
        query += AppendDomainQuery(domains, ref sep);
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<DashboardTimeSeriesPoint>>(query);
            return list ?? new List<DashboardTimeSeriesPoint>();
        });
    }

    public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
    {
        var query = BuildRangedQuery("/api/v1/topbots", count, startTime, endTime);
        if (!string.IsNullOrEmpty(audienceFilter))
            query += (query.Contains('?') ? "&" : "?") + $"audience={Uri.EscapeDataString(audienceFilter)}";
        var sep = query.Contains('?') ? '&' : '?';
        query += AppendDomainQuery(domains, ref sep);
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<DashboardTopBotEntry>>(query);
            return list ?? new List<DashboardTopBotEntry>();
        });
    }

    public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
    {
        var query = BuildRangedQuery("/api/v1/countries", count, startTime, endTime);
        if (!string.IsNullOrEmpty(audienceFilter))
            query += (query.Contains('?') ? "&" : "?") + $"audience={Uri.EscapeDataString(audienceFilter)}";
        var sep = query.Contains('?') ? '&' : '?';
        query += AppendDomainQuery(domains, ref sep);
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<DashboardCountryStats>>(query);
            return list ?? new List<DashboardCountryStats>();
        });
    }

    public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = $"/api/v1/countries/{Uri.EscapeDataString(countryCode)}"
            + BuildSinceUntil(startTime, endTime, prefix: "?");
        return GetOrFetchAsync(query, () => _api.GetEnvelopeAsync<DashboardCountryDetail>(query)!);
    }

    public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
    {
        var query = BuildRangedQuery("/api/v1/endpoints", count, startTime, endTime);
        if (!string.IsNullOrEmpty(audienceFilter))
            query += (query.Contains('?') ? "&" : "?") + $"audience={Uri.EscapeDataString(audienceFilter)}";
        var sep = query.Contains('?') ? '&' : '?';
        query += AppendDomainQuery(domains, ref sep);
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<DashboardEndpointStats>>(query);
            return list ?? new List<DashboardEndpointStats>();
        });
    }

    /// <summary>
    ///     Remote stub. The gateway's <c>/api/v1</c> surface does not yet expose
    ///     per-signature endpoint stats, so a remote-mode dashboard hosts an
    ///     empty list for this signature. The signature detail page falls back
    ///     to its existing Paths list when this returns empty, so no UI breaks.
    ///     A future API addition (route shape:
    ///     <c>/api/v1/signatures/{id}/endpoints</c>) can wire this through
    ///     without changing the call site on the dashboard side.
    /// </summary>
    public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(
        string signature, int topN = 25, CancellationToken ct = default)
        => Task.FromResult(new List<SignatureEndpointStats>());

    public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = $"/api/v1/endpoints/{Uri.EscapeDataString(method)}/{path.TrimStart('/')}"
            + BuildSinceUntil(startTime, endTime, prefix: "?");
        return GetOrFetchAsync(query, () => _api.GetEnvelopeAsync<DashboardEndpointDetail>(query)!);
    }

    public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = BuildRangedQuery("/api/v1/threats", count, startTime, endTime);
        return GetOrFetchAsync(query, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<ThreatEntry>>(query);
            return list ?? new List<ThreatEntry>();
        });
    }

    public async Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(
        int count = 50, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken ct = default)
    {
        // No remote API surface yet -- viewer can't aggregate honeypot hits
        // from the gateway. Returns empty so the tab renders an empty-state.
        await Task.CompletedTask;
        return new List<HoneypotHitRow>();
    }

    public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
    {
        var path = $"/api/v1/useragents/search?query={Uri.EscapeDataString(query ?? string.Empty)}&limit={limit}";
        return GetOrFetchAsync(path, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<UserAgentSearchResult>>(path);
            return list ?? new List<UserAgentSearchResult>();
        });
    }

    public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(
        string family, int hours = 168, CancellationToken ct = default)
    {
        var path = $"/api/v1/useragents/versions?family={Uri.EscapeDataString(family ?? string.Empty)}&hours={hours}";
        return GetOrFetchAsync(path, async () =>
        {
            var list = await _api.GetEnvelopeAsync<List<UserAgentVersionBucket>>(path);
            return list ?? new List<UserAgentVersionBucket>();
        });
    }

    /// <summary>
    ///     Single-round-trip override: POSTs the entire <see cref="DashboardBatchRequest"/>
    ///     to the gateway's <c>POST /api/v1/compose-batch</c> endpoint and deserializes the
    ///     <see cref="DashboardDatasetBundle"/> in one call.  Replaces the base-interface
    ///     default fan-out (N GET calls) with a single POST — the remote-viewer win.
    ///     On any transport or non-success response the gateway client logs a warning and
    ///     returns null; we degrade to an all-null bundle so the dashboard renders empty
    ///     widgets rather than surfacing an HTTP error.
    /// </summary>
    public async Task<DashboardDatasetBundle> ComposeBatchAsync(
        DashboardBatchRequest request, CancellationToken ct = default)
    {
        var bundle = await _api.PostEnvelopeAsync<DashboardBatchRequest, DashboardDatasetBundle>(
            "/api/v1/compose-batch", request, ct);
        return bundle ?? new DashboardDatasetBundle(null, null, null, null, null);
    }

    public async Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default)
    {
        var result = await _api.PostEnvelopeAsync<InvestigationFilter, InvestigationResult>(
            "/api/v1/investigate", filter, ct);
        return result ?? new InvestigationResult { Summary = new InvestigationSummary() };
    }

    /// <summary>
    ///     Site-health history -- the dashboard host reads the gateway's
    ///     persisted <c>degradation_history</c> rows over the existing
    ///     <c>GET /api/v1/site-health/history</c> endpoint. The endpoint
    ///     accepts a window token (15m / 1h / 24h / 6h / 12h) and returns
    ///     the slice oldest-first; we translate the caller's
    ///     <c>(startTime, endTime)</c> into the nearest canonical window so
    ///     the gateway-side parser stays unchanged.
    /// </summary>
    public async Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
        DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        var span = endTime - startTime;
        var window = span.TotalHours switch
        {
            <= 0.5 => "15m",
            <= 1.5 => "1h",
            <= 8.0 => "6h",
            <= 16.0 => "12h",
            _ => "24h",
        };
        var path = $"/api/v1/site-health/history?window={Uri.EscapeDataString(window)}";
        var list = await GetOrFetchAsync(path, async () =>
        {
            var l = await _api.GetEnvelopeListAsync<DegradationSnapshot>(path, ct);
            return l ?? new List<DegradationSnapshot>();
        }).ConfigureAwait(false);
        return list;
    }

    // === Write surface: not supported on the remote viewer ===

    public Task AddDetectionAsync(DashboardDetectionEvent detection)
        => throw new NotSupportedException("Detection writes are owned by the gateway.");

    public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
        => throw new NotSupportedException("Signature writes are owned by the gateway.");

    public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default)
        => throw new NotSupportedException("Bot-name updates are owned by the gateway.");

    public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
        => throw new NotSupportedException("Retention pruning is owned by the gateway.");

    public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default)
        => throw new NotSupportedException("Degradation snapshots are owned by the gateway.");

    // === Helpers ===

    private static string BuildRangedQuery(string path, int count, DateTime? startTime, DateTime? endTime)
    {
        var sb = new System.Text.StringBuilder(path);
        sb.Append("?limit=").Append(count);
        if (startTime.HasValue) sb.Append("&since=").Append(Uri.EscapeDataString(startTime.Value.ToString("o")));
        if (endTime.HasValue) sb.Append("&until=").Append(Uri.EscapeDataString(endTime.Value.ToString("o")));
        return sb.ToString();
    }

    private static string BuildSinceUntil(DateTime? since, DateTime? until, string prefix)
    {
        if (since is null && until is null) return string.Empty;
        var sb = new System.Text.StringBuilder(prefix);
        if (since.HasValue) sb.Append("since=").Append(Uri.EscapeDataString(since.Value.ToString("o")));
        if (until.HasValue)
        {
            if (sb.Length > prefix.Length) sb.Append('&');
            sb.Append("until=").Append(Uri.EscapeDataString(until.Value.ToString("o")));
        }
        return sb.ToString();
    }
}
