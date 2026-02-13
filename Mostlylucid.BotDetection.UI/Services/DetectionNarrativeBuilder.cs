using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds marketing-friendly narratives from detection data WITHOUT LLM (instant, every request).
///     Maps detector names to friendly categories and generates human-readable summaries.
/// </summary>
public static class DetectionNarrativeBuilder
{
    private static readonly Dictionary<string, string> DetectorFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserAgent"] = "user-agent analysis",
        ["Header"] = "HTTP header analysis",
        ["Ip"] = "IP reputation",
        ["SecurityTool"] = "security tool detection",
        ["Behavioral"] = "behavioral analysis",
        ["AdvancedBehavioral"] = "advanced behavioral analysis",
        ["ClientSide"] = "browser fingerprint",
        ["Inconsistency"] = "inconsistency detection",
        ["VersionAge"] = "browser version check",
        ["Heuristic"] = "AI heuristic model",
        ["HeuristicLate"] = "AI heuristic model",
        ["FastPathReputation"] = "reputation scoring",
        ["CacheBehavior"] = "cache behavior analysis",
        ["ReputationBias"] = "reputation bias",
        ["ProjectHoneypot"] = "honeypot DNS lookup",
        ["TlsFingerprint"] = "TLS fingerprint",
        ["TcpIpFingerprint"] = "TCP/IP fingerprint",
        ["Http2Fingerprint"] = "HTTP/2 fingerprint",
        ["Http3Fingerprint"] = "QUIC/HTTP3 fingerprint",
        ["MultiLayerCorrelation"] = "multi-layer correlation",
        ["BehavioralWaveform"] = "request timing analysis",
        ["ResponseBehavior"] = "response behavior analysis",
        ["Similarity"] = "similarity analysis",
        ["Llm"] = "LLM analysis",
        ["AI"] = "AI classification",
        ["AiScraper"] = "AI scraper detection"
    };

    private static readonly Dictionary<string, string> DetectorCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserAgent"] = "Browser",
        ["Header"] = "Network",
        ["Ip"] = "Network",
        ["SecurityTool"] = "Network",
        ["Behavioral"] = "Behavioral",
        ["AdvancedBehavioral"] = "Behavioral",
        ["ClientSide"] = "Fingerprint",
        ["Inconsistency"] = "Browser",
        ["VersionAge"] = "Browser",
        ["Heuristic"] = "AI",
        ["HeuristicLate"] = "AI",
        ["FastPathReputation"] = "Reputation",
        ["CacheBehavior"] = "Behavioral",
        ["ReputationBias"] = "Reputation",
        ["ProjectHoneypot"] = "Network",
        ["TlsFingerprint"] = "Fingerprint",
        ["TcpIpFingerprint"] = "Fingerprint",
        ["Http2Fingerprint"] = "Fingerprint",
        ["Http3Fingerprint"] = "Fingerprint",
        ["MultiLayerCorrelation"] = "Fingerprint",
        ["BehavioralWaveform"] = "Behavioral",
        ["ResponseBehavior"] = "Behavioral",
        ["Similarity"] = "Fingerprint",
        ["Llm"] = "AI",
        ["AI"] = "AI",
        ["AiScraper"] = "AI"
    };

    private static readonly Dictionary<string, string> BotTypeFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SearchEngine"] = "search engine crawler",
        ["SocialMediaBot"] = "social media bot",
        ["MonitoringBot"] = "monitoring bot",
        ["Scraper"] = "scraper",
        ["MaliciousBot"] = "malicious bot",
        ["GoodBot"] = "verified good bot",
        ["VerifiedBot"] = "verified bot",
        ["AiBot"] = "AI training bot"
    };

    /// <summary>
    ///     Build a marketing-friendly narrative from a detection event.
    /// </summary>
    public static string Build(DashboardDetectionEvent detection)
    {
        if (detection.IsBot)
            return BuildBotNarrative(detection);

        return BuildHumanNarrative(detection);
    }

    private static string BuildBotNarrative(DashboardDetectionEvent detection)
    {
        // Identify what kind of bot
        var botIdentity = GetBotIdentity(detection);

        // Get the top evidence (friendly names from TopReasons)
        var evidence = GetTopEvidence(detection, maxItems: 3);

        if (evidence.Count == 0)
            return $"{botIdentity} on {TruncatePath(detection.Path)}";

        return $"{botIdentity} on {TruncatePath(detection.Path)} - caught by {string.Join(", ", evidence)}";
    }

    private static string BuildHumanNarrative(DashboardDetectionEvent detection)
    {
        var evidence = GetTopEvidence(detection, maxItems: 2);

        var confidenceWord = detection.Confidence switch
        {
            >= 0.9 => "confirmed",
            >= 0.7 => "verified",
            >= 0.5 => "likely",
            _ => "probable"
        };

        var prefix = $"Real browser user ({confidenceWord})";

        if (evidence.Count == 0)
            return prefix;

        return $"{prefix} - {string.Join(", ", evidence)}";
    }

    private static string GetBotIdentity(DashboardDetectionEvent detection)
    {
        // If we have a specific bot name, use it
        if (!string.IsNullOrEmpty(detection.BotName))
            return detection.BotName;

        // If we have a bot type, map it to friendly name
        if (!string.IsNullOrEmpty(detection.BotType) &&
            BotTypeFriendlyNames.TryGetValue(detection.BotType, out var friendlyType))
            return char.ToUpper(friendlyType[0]) + friendlyType[1..];

        // Fallback based on risk band
        return detection.RiskBand switch
        {
            "VeryHigh" => "Suspicious automated client",
            "High" => "Likely bot",
            _ => "Possible bot"
        };
    }

    private static List<string> GetTopEvidence(DashboardDetectionEvent detection, int maxItems)
    {
        var evidence = new List<string>();

        // Use TopReasons directly - they are already human-readable strings from detectors
        foreach (var reason in detection.TopReasons.Take(maxItems))
        {
            if (string.IsNullOrWhiteSpace(reason))
                continue;

            // Shorten long reasons for the narrative
            var shortened = reason.Length > 60 ? reason[..57] + "..." : reason;
            evidence.Add(shortened);
        }

        return evidence;
    }

    /// <summary>
    ///     Get the friendly name for a detector.
    /// </summary>
    public static string GetDetectorFriendlyName(string detectorName)
    {
        return DetectorFriendlyNames.GetValueOrDefault(detectorName, detectorName);
    }

    /// <summary>
    ///     Get the category for a detector (Browser, Network, Behavioral, Fingerprint, AI, Reputation).
    /// </summary>
    public static string GetDetectorCategory(string detectorName)
    {
        return DetectorCategories.GetValueOrDefault(detectorName, "Other");
    }

    /// <summary>
    ///     Build a short ticker snippet for the header bar.
    ///     Examples: "Googlebot verified", "Scraper blocked (TLS)", "Human from UK"
    /// </summary>
    public static string BuildTickerSnippet(DashboardDetectionEvent detection)
    {
        if (detection.IsBot)
        {
            if (!string.IsNullOrEmpty(detection.BotName))
            {
                var action = detection.Action?.ToLowerInvariant() switch
                {
                    "block" or "Block" => "blocked",
                    "throttle-stealth" or "ThrottleStealth" => "throttled",
                    "challenge" or "Challenge" => "challenged",
                    _ => "detected"
                };
                return $"{detection.BotName} {action}";
            }

            var topReason = detection.TopReasons.FirstOrDefault();
            if (!string.IsNullOrEmpty(topReason))
            {
                var shortReason = topReason.Length > 30 ? topReason[..27] + "..." : topReason;
                return shortReason;
            }

            return $"Bot on {TruncatePath(detection.Path, 15)}";
        }

        // Human
        return $"Human on {TruncatePath(detection.Path, 15)}";
    }

    private static string TruncatePath(string? path, int maxLen = 20)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return path.Length > maxLen ? path[..Math.Max(maxLen - 3, 1)] + "..." : path;
    }
}
