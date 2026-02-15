using System.Collections.Concurrent;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Singleton that maintains a server-side cache of the latest visitors.
///     Updated by DetectionBroadcastMiddleware after each detection.
///     Provides filtered, sorted lists for HTMX rendering (same for all clients).
/// </summary>
public class VisitorListCache
{
    private readonly ConcurrentDictionary<string, CachedVisitor> _visitors = new();
    private readonly int _maxVisitors;

    public VisitorListCache(int maxVisitors = 100)
    {
        _maxVisitors = maxVisitors;
    }

    /// <summary>
    ///     Upsert a visitor from a detection event.
    ///     Called by DetectionBroadcastMiddleware after each detection.
    /// </summary>
    public CachedVisitor Upsert(DashboardDetectionEvent detection)
    {
        var sig = detection.PrimarySignature;
        if (string.IsNullOrEmpty(sig))
            sig = detection.RequestId;

        var visitor = _visitors.AddOrUpdate(sig,
            _ => new CachedVisitor
            {
                PrimarySignature = sig,
                Hits = 1,
                FirstSeen = detection.Timestamp,
                LastSeen = detection.Timestamp,
                IsBot = detection.IsBot,
                BotProbability = detection.BotProbability,
                Confidence = detection.Confidence,
                RiskBand = detection.RiskBand ?? "Medium",
                LastPath = detection.Path,
                Paths = new List<string> { detection.Path ?? "/" },
                Action = detection.Action ?? "Allow",
                BotName = detection.BotName,
                BotType = detection.BotType,
                CountryCode = detection.CountryCode,
                Narrative = detection.Narrative,
                Description = detection.Description,
                TopReasons = detection.TopReasons.ToList(),
                ProcessingTimeMs = detection.ProcessingTimeMs,
                LastRequestId = detection.RequestId
            },
            (_, existing) =>
            {
                existing.Hits++;
                existing.LastSeen = detection.Timestamp;
                existing.IsBot = detection.IsBot;
                existing.BotProbability = detection.BotProbability;
                existing.Confidence = detection.Confidence;
                existing.RiskBand = detection.RiskBand ?? existing.RiskBand;
                existing.LastPath = detection.Path;
                existing.Action = detection.Action ?? existing.Action;
                if (!string.IsNullOrEmpty(detection.Narrative))
                    existing.Narrative = detection.Narrative;
                if (!string.IsNullOrEmpty(detection.Description))
                    existing.Description = detection.Description;
                if (detection.TopReasons.Count > 0)
                    existing.TopReasons = detection.TopReasons.ToList();
                if (!string.IsNullOrEmpty(detection.BotName))
                    existing.BotName = detection.BotName;
                if (!string.IsNullOrEmpty(detection.BotType))
                    existing.BotType = detection.BotType;
                if (!string.IsNullOrEmpty(detection.CountryCode))
                    existing.CountryCode = detection.CountryCode;
                existing.ProcessingTimeMs = detection.ProcessingTimeMs;
                existing.LastRequestId = detection.RequestId;
                if (!string.IsNullOrEmpty(detection.Path) && !existing.Paths.Contains(detection.Path))
                {
                    existing.Paths.Add(detection.Path);
                    if (existing.Paths.Count > 5)
                        existing.Paths.RemoveAt(0);
                }
                return existing;
            });

        EvictOldest();
        return visitor;
    }

    /// <summary>
    ///     Get filtered, sorted, sliced list for HTMX rendering.
    /// </summary>
    public IReadOnlyList<CachedVisitor> GetFiltered(string? filter, string sortField, string sortDir, int limit = 50)
    {
        IEnumerable<CachedVisitor> items = _visitors.Values;

        items = filter switch
        {
            "humans" => items.Where(v => !v.IsBot),
            "bots" => items.Where(v => v.IsBot),
            "ai" => items.Where(v => v.IsBot && (v.BotType == "AiBot" || IsAiName(v.BotName))),
            "tools" => items.Where(v => v.IsBot && IsToolType(v.BotType)),
            _ => items
        };

        items = (sortField, sortDir) switch
        {
            ("name", "asc") => items.OrderBy(v => v.BotName ?? v.PrimarySignature),
            ("name", _) => items.OrderByDescending(v => v.BotName ?? v.PrimarySignature),
            ("hits", "asc") => items.OrderBy(v => v.Hits),
            ("hits", _) => items.OrderByDescending(v => v.Hits),
            ("risk", "asc") => items.OrderBy(v => RiskOrder(v.RiskBand)),
            ("risk", _) => items.OrderByDescending(v => RiskOrder(v.RiskBand)),
            (_, "asc") => items.OrderBy(v => v.LastSeen),
            _ => items.OrderByDescending(v => v.LastSeen)
        };

        return items.Take(limit).ToList();
    }

    /// <summary>
    ///     Get a single visitor by signature.
    /// </summary>
    public CachedVisitor? Get(string primarySignature)
    {
        return _visitors.TryGetValue(primarySignature, out var v) ? v : null;
    }

    /// <summary>
    ///     Get filter badge counts.
    /// </summary>
    public FilterCounts GetCounts()
    {
        var all = _visitors.Values;
        return new FilterCounts
        {
            All = all.Count,
            Humans = all.Count(v => !v.IsBot),
            Bots = all.Count(v => v.IsBot),
            Ai = all.Count(v => v.IsBot && (v.BotType == "AiBot" || IsAiName(v.BotName))),
            Tools = all.Count(v => v.IsBot && IsToolType(v.BotType))
        };
    }

    /// <summary>
    ///     Get top N bots by hit count.
    /// </summary>
    public IReadOnlyList<CachedVisitor> GetTopBots(int count = 5)
    {
        return _visitors.Values
            .Where(v => v.IsBot)
            .OrderByDescending(v => v.Hits)
            .Take(count)
            .ToList();
    }

    private void EvictOldest()
    {
        if (_visitors.Count <= _maxVisitors) return;

        var toRemove = _visitors
            .OrderBy(kv => kv.Value.LastSeen)
            .Take(_visitors.Count - _maxVisitors)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            _visitors.TryRemove(key, out _);
    }

    private static bool IsAiName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        System.Text.RegularExpressions.Regex.IsMatch(name, @"ai|gpt|claude|llm|chatbot|copilot|gemini|bard",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsToolType(string? botType) =>
        botType is "Scraper" or "MonitoringBot" or "SearchEngine" or "SocialMediaBot" or "VerifiedBot" or "GoodBot";

    private static int RiskOrder(string? band) => band switch
    {
        "VeryHigh" => 5, "High" => 4, "Medium" or "Elevated" => 3, "Low" => 2, "VeryLow" => 1, _ => 0
    };
}

/// <summary>
///     A cached visitor entry for HTMX rendering.
/// </summary>
public class CachedVisitor
{
    public required string PrimarySignature { get; set; }
    public int Hits { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public double Confidence { get; set; }
    public string RiskBand { get; set; } = "Medium";
    public string? LastPath { get; set; }
    public List<string> Paths { get; set; } = new();
    public string Action { get; set; } = "Allow";
    public string? BotName { get; set; }
    public string? BotType { get; set; }
    public string? CountryCode { get; set; }
    public string? Narrative { get; set; }
    public string? Description { get; set; }
    public List<string> TopReasons { get; set; } = new();
    public double ProcessingTimeMs { get; set; }
    public string? LastRequestId { get; set; }

    public string TimeAgo
    {
        get
        {
            var seconds = (int)(DateTime.UtcNow - LastSeen).TotalSeconds;
            if (seconds < 5) return "now";
            if (seconds < 60) return $"{seconds}s";
            if (seconds < 3600) return $"{seconds / 60}m";
            return $"{seconds / 3600}h";
        }
    }
}

/// <summary>
///     Filter button badge counts.
/// </summary>
public class FilterCounts
{
    public int All { get; set; }
    public int Humans { get; set; }
    public int Bots { get; set; }
    public int Ai { get; set; }
    public int Tools { get; set; }
}
