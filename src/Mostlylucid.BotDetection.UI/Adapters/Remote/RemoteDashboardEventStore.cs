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
/// </summary>
internal sealed class RemoteDashboardEventStore : IDashboardEventStore
{
    private readonly GatewayApiClient _api;

    public RemoteDashboardEventStore(GatewayApiClient api) => _api = api;

    public async Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
    {
        var f = filter ?? new DashboardFilter();
        var query = $"/api/v1/detections?limit={f.Limit}&offset={f.Offset}"
            + (f.IsBot.HasValue ? $"&isBot={f.IsBot.Value.ToString().ToLowerInvariant()}" : "")
            + (f.StartTime.HasValue ? $"&since={Uri.EscapeDataString(f.StartTime.Value.ToString("o"))}" : "");
        var list = await _api.GetEnvelopeAsync<List<DashboardDetectionEvent>>(query, ct);
        return list ?? new List<DashboardDetectionEvent>();
    }

    public async Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
    {
        var query = $"/api/v1/signatures?limit={limit}&offset={offset}"
            + (isBot.HasValue ? $"&isBot={isBot.Value.ToString().ToLowerInvariant()}" : "");
        var list = await _api.GetEnvelopeAsync<List<DashboardSignatureEvent>>(query);
        return list ?? new List<DashboardSignatureEvent>();
    }

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        var summary = await _api.GetEnvelopeAsync<DashboardSummary>("/api/v1/summary");
        return summary ?? EmptySummary();
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

    public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize)
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
        var list = await _api.GetEnvelopeAsync<List<DashboardTimeSeriesPoint>>(query);
        return list ?? new List<DashboardTimeSeriesPoint>();
    }

    public async Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = BuildRangedQuery("/api/v1/topbots", count, startTime, endTime);
        var list = await _api.GetEnvelopeAsync<List<DashboardTopBotEntry>>(query);
        return list ?? new List<DashboardTopBotEntry>();
    }

    public async Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = BuildRangedQuery("/api/v1/countries", count, startTime, endTime);
        var list = await _api.GetEnvelopeAsync<List<DashboardCountryStats>>(query);
        return list ?? new List<DashboardCountryStats>();
    }

    public async Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = $"/api/v1/countries/{Uri.EscapeDataString(countryCode)}"
            + BuildSinceUntil(startTime, endTime, prefix: "?");
        return await _api.GetEnvelopeAsync<DashboardCountryDetail>(query);
    }

    public async Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = BuildRangedQuery("/api/v1/endpoints", count, startTime, endTime);
        var list = await _api.GetEnvelopeAsync<List<DashboardEndpointStats>>(query);
        return list ?? new List<DashboardEndpointStats>();
    }

    public async Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = $"/api/v1/endpoints/{Uri.EscapeDataString(method)}/{path.TrimStart('/')}"
            + BuildSinceUntil(startTime, endTime, prefix: "?");
        return await _api.GetEnvelopeAsync<DashboardEndpointDetail>(query);
    }

    public async Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = BuildRangedQuery("/api/v1/threats", count, startTime, endTime);
        var list = await _api.GetEnvelopeAsync<List<ThreatEntry>>(query);
        return list ?? new List<ThreatEntry>();
    }

    public async Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
    {
        var path = $"/api/v1/useragents/search?query={Uri.EscapeDataString(query ?? string.Empty)}&limit={limit}";
        var list = await _api.GetEnvelopeAsync<List<UserAgentSearchResult>>(path);
        return list ?? new List<UserAgentSearchResult>();
    }

    public async Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default)
    {
        var result = await _api.PostEnvelopeAsync<InvestigationFilter, InvestigationResult>(
            "/api/v1/investigate", filter, ct);
        return result ?? new InvestigationResult { Summary = new InvestigationSummary() };
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
