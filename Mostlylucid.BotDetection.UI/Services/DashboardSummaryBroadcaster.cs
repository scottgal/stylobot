using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service that periodically computes all dashboard aggregates
///     and broadcasts summary statistics to connected clients.
///     This is the SINGLE place where top bots, countries, and user agents are computed.
///     API endpoints read from <see cref="DashboardAggregateCache" /> — no inline computation.
/// </summary>
public class DashboardSummaryBroadcaster : BackgroundService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly DashboardAggregateCache _cache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<DashboardSummaryBroadcaster> _logger;
    private readonly StyloBotDashboardOptions _options;
    private bool _seeded;

    public DashboardSummaryBroadcaster(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        DashboardAggregateCache cache,
        SignatureAggregateCache signatureCache,
        StyloBotDashboardOptions options,
        ILogger<DashboardSummaryBroadcaster> logger)
    {
        _hubContext = hubContext;
        _eventStore = eventStore;
        _cache = cache;
        _signatureCache = signatureCache;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Dashboard broadcaster started (interval: {Interval}s)",
            _options.SummaryBroadcastIntervalSeconds);

        // Wait briefly for database schema initialization to complete before querying.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                // Seed SignatureAggregateCache from DB on first iteration
                if (!_seeded)
                {
                    _seeded = true;
                    try
                    {
                        var seedBots = await _eventStore.GetTopBotsAsync(100);
                        _signatureCache.SeedFromTopBots(seedBots);
                        _logger.LogInformation("Seeded SignatureAggregateCache with {Count} entries from DB", seedBots.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to seed SignatureAggregateCache from DB");
                    }
                }

                // Compute aggregates from DB in parallel (no TopBots — handled by write-through cache)
                var summaryTask = _eventStore.GetSummaryAsync();
                var countriesTask = _eventStore.GetCountryStatsAsync(50);
                var userAgentsTask = ComputeUserAgentsAsync();

                await Task.WhenAll(summaryTask, countriesTask, userAgentsTask);

                // Update cache atomically
                _cache.Update(new DashboardAggregateCache.AggregateSnapshot
                {
                    Countries = await countriesTask,
                    UserAgents = await userAgentsTask
                });

                // Send lightweight invalidation signals — the HTMX coordinator
                // will fetch fresh server-rendered partials on demand.
                // No need to serialize full data payloads over the wire.
                await _hubContext.Clients.All.BroadcastInvalidation("summary");
                await _hubContext.Clients.All.BroadcastInvalidation("countries");
                await _hubContext.Clients.All.BroadcastInvalidation("signature");
                await _hubContext.Clients.All.BroadcastInvalidation("useragents");

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.SummaryBroadcastIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing dashboard aggregates");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

        _logger.LogInformation("Dashboard broadcaster stopped");
    }

    /// <summary>
    ///     Compute user agent aggregates from detections.
    ///     This logic was previously inline in ServeUserAgentsApiAsync — now computed once per beacon tick.
    /// </summary>
    private async Task<List<DashboardUserAgentSummary>> ComputeUserAgentsAsync()
    {
        var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter { Limit = 500 });

        var uaGroups = new Dictionary<string, (int total, int bot, int human, double confSum, double procSum,
            DateTime lastSeen, Dictionary<string, int> versions, Dictionary<string, int> countries)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var d in detections)
        {
            var signals = d.ImportantSignals;
            string? family = null;
            string? version = null;

            if (signals != null)
            {
                if (signals.TryGetValue("ua.family", out var ff)) family = ff?.ToString();
                if (signals.TryGetValue("ua.family_version", out var fv)) version = fv?.ToString();
                if (string.IsNullOrEmpty(family) && signals.TryGetValue("ua.bot_name", out var bn))
                    family = bn?.ToString();
            }

            if (string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(d.UserAgent))
                family = ExtractBrowserFamily(d.UserAgent);

            if (string.IsNullOrEmpty(family))
                family = "Unknown";

            if (!uaGroups.TryGetValue(family, out var group))
                group = (0, 0, 0, 0, 0, DateTime.MinValue,
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            group.total++;
            if (d.IsBot) group.bot++;
            else group.human++;
            group.confSum += d.Confidence;
            group.procSum += d.ProcessingTimeMs;
            if (d.Timestamp > group.lastSeen) group.lastSeen = d.Timestamp;

            if (!string.IsNullOrEmpty(version))
            {
                group.versions.TryGetValue(version, out var vc);
                group.versions[version] = vc + 1;
            }

            if (!string.IsNullOrEmpty(d.CountryCode))
            {
                group.countries.TryGetValue(d.CountryCode, out var cc);
                group.countries[d.CountryCode] = cc + 1;
            }

            uaGroups[family] = group;
        }

        return uaGroups
            .Select(kv => new DashboardUserAgentSummary
            {
                Family = kv.Key,
                Category = InferUaCategory(kv.Key),
                TotalCount = kv.Value.total,
                BotCount = kv.Value.bot,
                HumanCount = kv.Value.human,
                BotRate = kv.Value.total > 0 ? Math.Round((double)kv.Value.bot / kv.Value.total, 4) : 0,
                Versions = kv.Value.versions,
                Countries = kv.Value.countries,
                AvgConfidence = kv.Value.total > 0 ? Math.Round(kv.Value.confSum / kv.Value.total, 4) : 0,
                AvgProcessingTimeMs = kv.Value.total > 0 ? Math.Round(kv.Value.procSum / kv.Value.total, 2) : 0,
                LastSeen = kv.Value.lastSeen,
            })
            .OrderByDescending(u => u.TotalCount)
            .ToList();
    }

    // ─── Shared classification helpers ───────────────────────────────────

    internal static string ExtractBrowserFamily(string ua)
    {
        if (ua.Contains("Edg/", StringComparison.Ordinal)) return "Edge";
        if (ua.Contains("OPR/", StringComparison.Ordinal) || ua.Contains("Opera", StringComparison.Ordinal)) return "Opera";
        if (ua.Contains("Firefox/", StringComparison.Ordinal)) return "Firefox";
        if (ua.Contains("Chrome/", StringComparison.Ordinal)) return "Chrome";
        if (ua.Contains("Safari/", StringComparison.Ordinal) && !ua.Contains("Chrome/", StringComparison.Ordinal)) return "Safari";
        if (ua.Contains("curl/", StringComparison.OrdinalIgnoreCase)) return "curl";
        if (ua.Contains("python", StringComparison.OrdinalIgnoreCase)) return "Python";
        if (ua.Contains("Go-http-client", StringComparison.Ordinal)) return "Go";
        if (ua.Contains("Googlebot", StringComparison.OrdinalIgnoreCase)) return "Googlebot";
        if (ua.Contains("bingbot", StringComparison.OrdinalIgnoreCase)) return "Bingbot";
        if (ua.Contains("GPTBot", StringComparison.OrdinalIgnoreCase)) return "GPTBot";
        if (ua.Contains("ClaudeBot", StringComparison.OrdinalIgnoreCase)) return "ClaudeBot";
        return "Other";
    }

    internal static string InferUaCategory(string family)
    {
        var f = family.ToLowerInvariant();
        if (f is "chrome" or "firefox" or "safari" or "edge" or "opera" or "brave" or "vivaldi" or "samsung internet")
            return "browser";
        if (f is "googlebot" or "bingbot" or "yandexbot" or "baiduspider" or "duckduckbot")
            return "search";
        if (f is "gptbot" or "claudebot" or "ccbot" or "perplexitybot" or "bytespider")
            return "ai";
        if (f is "curl" or "python" or "go" or "java" or "node" or "wget" or "other")
            return "tool";
        return "unknown";
    }
}
