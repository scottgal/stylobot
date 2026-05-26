using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Stateless service that queries <see cref="IDashboardEventStore" /> and folds the
///     raw detection rows into per-UA-family summaries.
///     <para>
///     Used by both <see cref="DashboardSummaryBroadcaster" /> (no-arg call, preserves
///     existing beacon-tick behavior) and dashboard view components that need to honour
///     <c>audience</c> / time-range parameters without touching the pre-aggregated cache.
///     </para>
/// </summary>
public sealed class DashboardUserAgentAggregator(IDashboardEventStore eventStore)
{
    public async Task<List<DashboardUserAgentSummary>> ComputeAsync(
        string? audienceFilter = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        var isBot = audienceFilter?.ToLowerInvariant() switch
        {
            "humans" => (bool?)false,
            "bots"   => true,
            _        => null,
        };

        var detections = await eventStore.GetDetectionsAsync(new DashboardFilter
        {
            Limit     = 500,
            StartTime = startTime,
            EndTime   = endTime,
            IsBot     = isBot,
        });

        var uaGroups = new Dictionary<string, (int total, int bot, int human, double confSum, double procSum,
            double maxProcMs, DateTime lastSeen, Dictionary<string, int> versions, Dictionary<string, int> countries, long bytesOut)>(
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

            // UserAgent is the in-flight SignalR property; UserAgentRaw is the DB-persisted column.
            // Fall back through both so DB-loaded detections (which only populate UserAgentRaw)
            // still get a family assigned.
            var rawUa = d.UserAgent ?? d.UserAgentRaw;
            if (string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(rawUa))
                family = ExtractBrowserFamily(rawUa);

            if (string.IsNullOrEmpty(family))
                family = "Unknown";

            if (!uaGroups.TryGetValue(family, out var group))
                group = (0, 0, 0, 0, 0, 0, DateTime.MinValue,
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    0L);

            group.total++;
            if (d.IsBot) group.bot++;
            else group.human++;
            group.confSum += d.Confidence;
            group.procSum += d.ProcessingTimeMs;
            group.maxProcMs = Math.Max(group.maxProcMs, d.ProcessingTimeMs);
            group.bytesOut += d.ResponseBytes ?? 0;
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
            .Select(kv =>
            {
                var avgMs = kv.Value.total > 0 ? kv.Value.procSum / kv.Value.total : 0;
                var maxMs = kv.Value.maxProcMs;
                // p95 approximation: avg + 90% of the gap to max. Matches the Postgres
                // backend convention; real percentile requires PERCENTILE_CONT (Task 10).
                var p95Ms = avgMs + (maxMs - avgMs) * 0.9;
                return new DashboardUserAgentSummary
                {
                    Family              = kv.Key,
                    Category            = InferUaCategory(kv.Key),
                    TotalCount          = kv.Value.total,
                    BotCount            = kv.Value.bot,
                    HumanCount          = kv.Value.human,
                    BotRate             = kv.Value.total > 0 ? Math.Round((double)kv.Value.bot / kv.Value.total, 4) : 0,
                    Versions            = kv.Value.versions,
                    Countries           = kv.Value.countries,
                    AvgConfidence       = kv.Value.total > 0 ? Math.Round(kv.Value.confSum / kv.Value.total, 4) : 0,
                    AvgProcessingTimeMs = Math.Round(avgMs, 2),
                    LastSeen            = kv.Value.lastSeen,
                    BytesOut            = kv.Value.bytesOut,
                    P95ProcessingTimeMs = Math.Round(p95Ms, 2),
                };
            })
            .OrderByDescending(u => u.TotalCount)
            .ToList();
    }

    // ─── Classification helpers ───────────────────────────────────────────

    internal static string ExtractBrowserFamily(string ua)
        => Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(ua).Family;

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
