using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Dynamic feature extraction for ML-based bot detection.
///     Features are extracted as key-value pairs, allowing the model to learn new features dynamically.
/// </summary>
/// <remarks>
///     <para>
///         <b>DYNAMIC FEATURES:</b> Features are extracted as a dictionary of name→value pairs.
///         This allows the model to discover and learn from any features present in the data,
///         rather than being constrained to a fixed vector size.
///     </para>
///     <para>
///         Key design principle: This extractor ONLY consumes evidence from other detectors.
///         It does not perform its own detection logic. Features include:
///         <list type="bullet">
///             <item>Basic request metadata (headers, UA, cookies)</item>
///             <item>Detector results from AggregatedEvidence (named by detector)</item>
///             <item>Category breakdown scores (named by category)</item>
///             <item>Aggregated statistics</item>
///             <item>Signal presence indicators (named by signal type)</item>
///         </list>
///     </para>
///     <para>
///         IMPORTANT: Feature names are derived from the actual data (detector names, category names,
///         signal types), making the system fully dynamic. New detectors automatically create new features.
///     </para>
/// </remarks>
public static class HeuristicFeatureExtractor
{
    /// <summary>
    ///     Extracts features as a dynamic dictionary from the HTTP context and aggregated evidence.
    ///     This is the primary entry point for full mode (after all detectors have run).
    /// </summary>
    /// <remarks>
    ///     Feature names are derived dynamically from detector names, category names, and signal types.
    ///     This allows the model to automatically learn from new detectors without code changes.
    /// </remarks>
    public static Dictionary<string, float> ExtractFeatures(HttpContext context, AggregatedEvidence evidence)
    {
        var features = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // === Basic Request Metadata ===
        ExtractRequestMetadata(context, features);

        // === Detector Results (named by actual detector) ===
        ExtractDetectorResults(evidence, features);

        // === Category Breakdown (named by actual category) ===
        ExtractCategoryBreakdown(evidence, features);

        // === Signal Presence (named by actual signal type) ===
        ExtractSignalPresence(evidence, features);

        // === AI/LLM Results (extract actual values, not just presence) ===
        ExtractAiResults(evidence, features);

        // === Aggregated Statistics ===
        ExtractStatistics(evidence, features);

        // === Final Results ===
        ExtractFinalResults(evidence, features);

        return features;
    }

    /// <summary>
    ///     Extracts basic request metadata features for early mode detection.
    ///     Used when AggregatedEvidence is not yet available.
    /// </summary>
    public static Dictionary<string, float> ExtractBasicFeatures(HttpContext context)
    {
        var features = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        ExtractRequestMetadata(context, features);
        return features;
    }

    /// <summary>
    ///     Extracts basic request metadata (raw data only, no detection).
    /// </summary>
    private static void ExtractRequestMetadata(HttpContext context, Dictionary<string, float> features)
    {
        var headers = context.Request.Headers;
        var userAgent = headers.UserAgent.ToString();

        // Request basics
        features["req:ua_length"] = Math.Min(userAgent.Length / 200f, 1f);
        features["req:path_length"] = Math.Min((context.Request.Path.Value?.Length ?? 0) / 100f, 1f);
        features["req:query_count"] = Math.Min(context.Request.Query.Count / 10f, 1f);
        features["req:content_length"] = Math.Min((context.Request.ContentLength ?? 0) / 10000f, 1f);
        features["req:is_https"] = context.Request.IsHttps ? 1f : 0f;
        features["req:header_count"] = Math.Min(headers.Count / 20f, 1f);
        features["req:cookie_count"] = Math.Min(context.Request.Cookies.Count / 10f, 1f);

        // Header presence (using header name as feature key)
        features["hdr:accept-language"] = headers.ContainsKey("Accept-Language") ? 1f : 0f;
        features["hdr:accept"] = headers.ContainsKey("Accept") ? 1f : 0f;
        features["hdr:referer"] = headers.ContainsKey("Referer") ? 1f : 0f;
        features["hdr:origin"] = headers.ContainsKey("Origin") ? 1f : 0f;
        features["hdr:x-requested-with"] = headers["X-Requested-With"].ToString()
            .Equals("XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            ? 1f
            : 0f;
        features["hdr:connection-close"] = headers.Connection.ToString()
            .Equals("close", StringComparison.OrdinalIgnoreCase)
            ? 1f
            : 0f;

        // User-Agent pattern features (dynamic based on content)
        var uaLower = userAgent.ToLowerInvariant();
        if (uaLower.Contains("bot")) features["ua:contains_bot"] = 1f;
        if (uaLower.Contains("spider")) features["ua:contains_spider"] = 1f;
        if (uaLower.Contains("crawler")) features["ua:contains_crawler"] = 1f;
        if (uaLower.Contains("scraper")) features["ua:contains_scraper"] = 1f;
        if (uaLower.Contains("headless")) features["ua:headless"] = 1f;
        if (uaLower.Contains("phantomjs")) features["ua:phantomjs"] = 1f;
        if (uaLower.Contains("selenium")) features["ua:selenium"] = 1f;
        if (uaLower.Contains("chrome")) features["ua:chrome"] = 1f;
        if (uaLower.Contains("firefox")) features["ua:firefox"] = 1f;
        if (uaLower.Contains("safari")) features["ua:safari"] = 1f;
        if (uaLower.Contains("edge")) features["ua:edge"] = 1f;
        if (uaLower.Contains("curl")) features["ua:curl"] = 1f;
        if (uaLower.Contains("wget")) features["ua:wget"] = 1f;
        if (uaLower.Contains("python")) features["ua:python"] = 1f;
        if (uaLower.Contains("scrapy")) features["ua:scrapy"] = 1f;
        if (uaLower.Contains("requests")) features["ua:requests"] = 1f;
        if (uaLower.Contains("httpx")) features["ua:httpx"] = 1f;
        if (uaLower.Contains("aiohttp")) features["ua:aiohttp"] = 1f;

        // Accept header analysis
        var accept = headers.Accept.ToString();
        if (accept == "*/*") features["accept:wildcard"] = 1f;
        if (accept.Contains("text/html")) features["accept:html"] = 1f;
        if (accept.Contains("application/json")) features["accept:json"] = 1f;
    }

    /// <summary>
    ///     Extracts detector results using actual detector names as feature keys.
    /// </summary>
    private static void ExtractDetectorResults(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        // Group by detector name and get max confidence
        var detectorResults = evidence.Contributions
            .GroupBy(c => NormalizeKey(c.DetectorName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => $"det:{g.Key}",
                g => (float)g.Max(c => c.ConfidenceDelta),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in detectorResults) features[key] = value;

        // Also store absolute confidence for ranking
        var absResults = evidence.Contributions
            .GroupBy(c => NormalizeKey(c.DetectorName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => $"det_abs:{g.Key}",
                g => (float)g.Max(c => Math.Abs(c.ConfidenceDelta)),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in absResults) features[key] = value;
    }

    /// <summary>
    ///     Extracts category breakdown using actual category names as feature keys.
    /// </summary>
    private static void ExtractCategoryBreakdown(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        foreach (var (category, breakdown) in evidence.CategoryBreakdown)
        {
            var key = NormalizeKey(category);
            features[$"cat:{key}:score"] = (float)breakdown.Score;
            features[$"cat:{key}:count"] = Math.Min(breakdown.ContributionCount / 5f, 1f);
        }
    }

    /// <summary>
    ///     Extracts signal presence using actual signal types as feature keys.
    /// </summary>
    private static void ExtractSignalPresence(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        // Signal presence indicators
        foreach (var signal in evidence.Signals)
        {
            var key = NormalizeKey(signal.Key);
            features[$"sig:{key}"] = 1f;
        }

        // Failed detector indicators
        foreach (var failed in evidence.FailedDetectors)
        {
            var key = NormalizeKey(failed);
            features[$"fail:{key}"] = 1f;
        }

        // Client-side fingerprint specific features - this is a STRONG human indicator
        var hasClientSide = evidence.Contributions.Any(c =>
            c.DetectorName.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
            c.Category.Equals("ClientSide", StringComparison.OrdinalIgnoreCase));

        var clientSideContrib = evidence.Contributions
            .FirstOrDefault(c => c.Category.Equals("ClientSide", StringComparison.OrdinalIgnoreCase));

        if (hasClientSide && clientSideContrib != null)
        {
            // Fingerprint was received - strong human indicator
            features["fp:received"] = 1f;

            // If the detector gave a negative confidence (human-like), that's very strong
            if (clientSideContrib.ConfidenceDelta < 0)
            {
                features["fp:legitimate"] = 1f;
                features["fp:integrity"] = (float)Math.Abs(clientSideContrib.ConfidenceDelta);
            }
            else if (clientSideContrib.ConfidenceDelta > 0.3)
            {
                // Suspicious fingerprint (headless, etc.)
                features["fp:suspicious"] = 1f;
            }
        }
        else
        {
            // No fingerprint - slightly suspicious but not conclusive
            features["fp:missing"] = 1f;
        }
    }

    /// <summary>
    ///     Extracts AI/LLM detector results as numeric features.
    ///     This provides the actual prediction values, not just presence indicators.
    ///     Critical for late heuristic to incorporate AI feedback.
    /// </summary>
    private static void ExtractAiResults(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        // Check if AI ran
        if (!evidence.AiRan)
        {
            features["ai:ran"] = 0f;
            return;
        }

        features["ai:ran"] = 1f;

        // Extract AI prediction (bot = 1, human = 0)
        if (evidence.Signals.TryGetValue(SignalKeys.AiPrediction, out var prediction))
        {
            var isBot = prediction is string s && s.Equals("bot", StringComparison.OrdinalIgnoreCase);
            features["ai:prediction"] = isBot ? 1f : 0f;
        }

        // Extract AI confidence as numeric value
        if (evidence.Signals.TryGetValue(SignalKeys.AiConfidence, out var confidenceObj))
        {
            var confidence = confidenceObj switch
            {
                double d => (float)d,
                float f => f,
                int i => i / 100f,
                _ => 0.5f
            };
            features["ai:confidence"] = confidence;

            // Also extract AI's contribution to bot probability
            // If AI said human with high confidence, that's a strong negative (human) signal
            var aiPrediction = features.GetValueOrDefault("ai:prediction", 0.5f);
            if (aiPrediction < 0.5f) // Human prediction
            {
                // Convert confidence to negative (human-leaning) feature
                features["ai:human_confidence"] = confidence;
                features["ai:bot_confidence"] = 0f;
            }
            else // Bot prediction
            {
                features["ai:human_confidence"] = 0f;
                features["ai:bot_confidence"] = confidence;
            }
        }

        // Get the actual confidence delta from the LLM contribution
        var llmContribution = evidence.Contributions
            .FirstOrDefault(c => c.DetectorName.Equals("Llm", StringComparison.OrdinalIgnoreCase));

        if (llmContribution != null)
        {
            // Store the signed delta (negative = human, positive = bot)
            features["ai:delta"] = (float)llmContribution.ConfidenceDelta;
            features["ai:weight"] = (float)llmContribution.Weight;
        }
    }

    /// <summary>
    ///     Extracts aggregated statistics from evidence.
    /// </summary>
    private static void ExtractStatistics(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        var detectorScores = evidence.Contributions
            .GroupBy(c => c.DetectorName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Max(c => Math.Abs(c.ConfidenceDelta)))
            .ToList();

        var categoryScores = evidence.CategoryBreakdown.Values
            .Select(c => c.Score)
            .ToList();

        // Detector stats
        features["stat:detector_count"] = Math.Min(detectorScores.Count / 10f, 1f);
        features["stat:detector_flagged"] = Math.Min(detectorScores.Count(s => s > 0.3) / 6f, 1f);
        features["stat:detector_max"] = detectorScores.Count > 0 ? (float)detectorScores.Max() : 0f;
        features["stat:detector_avg"] = detectorScores.Count > 0 ? (float)detectorScores.Average() : 0f;
        features["stat:detector_variance"] = detectorScores.Count > 1 ? (float)CalculateVariance(detectorScores) : 0f;

        // Category stats
        features["stat:category_count"] = Math.Min(categoryScores.Count / 8f, 1f);
        features["stat:category_max"] = categoryScores.Count > 0 ? (float)categoryScores.Max() : 0f;
        features["stat:category_avg"] = categoryScores.Count > 0 ? (float)categoryScores.Average() : 0f;

        // Other stats
        features["stat:contribution_count"] = Math.Min(evidence.Contributions.Count / 20f, 1f);
        features["stat:signal_count"] = Math.Min(evidence.Signals.Count / 50f, 1f);
        features["stat:failed_count"] = Math.Min(evidence.FailedDetectors.Count / 5f, 1f);
        features["stat:processing_time"] = Math.Min((float)evidence.TotalProcessingTimeMs / 1000f, 1f);
    }

    /// <summary>
    ///     Extracts final aggregated results.
    /// </summary>
    private static void ExtractFinalResults(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        features["result:bot_probability"] = (float)evidence.BotProbability;
        features["result:confidence"] = (float)evidence.Confidence;
        features["result:early_exit"] = evidence.EarlyExit ? 1f : 0f;
        features["result:risk_band"] = (int)evidence.RiskBand / 5f;

        if (evidence.PrimaryBotType.HasValue)
            features[$"bottype:{evidence.PrimaryBotType.Value.ToString().ToLowerInvariant()}"] = 1f;

        if (!string.IsNullOrEmpty(evidence.PrimaryBotName))
            features[$"botname:{NormalizeKey(evidence.PrimaryBotName)}"] = 1f;
    }

    /// <summary>
    ///     Normalizes a key for use in feature names.
    ///     Removes spaces, converts to lowercase, replaces special characters.
    /// </summary>
    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "unknown";

        return key
            .ToLowerInvariant()
            .Replace(" ", "_")
            .Replace("-", "_")
            .Replace(".", "_")
            .Replace(":", "_");
    }

    private static double CalculateVariance(List<double> values)
    {
        if (values.Count < 2) return 0;
        var avg = values.Average();
        return values.Sum(v => Math.Pow(v - avg, 2)) / values.Count;
    }
}

/// <summary>
///     Extended behavioral metrics for ML feature extraction.
///     Populated by behavioral detectors and passed via signals.
/// </summary>
public class BehavioralMetrics
{
    public double? RequestsPerMinute { get; set; }
    public double? UniquePathsRatio { get; set; }
    public double? SessionDurationSeconds { get; set; }
    public double? AvgTimeBetweenRequestsMs { get; set; }
    public double? TimeVariance { get; set; }
    public double? SequentialAccessScore { get; set; }
    public double? DepthFirstScore { get; set; }
    public double? BreadthFirstScore { get; set; }
    public double? RandomAccessScore { get; set; }
    public double? ErrorRate { get; set; }
    public double? StaticResourceRatio { get; set; }
    public double? ApiRequestRatio { get; set; }
}

/// <summary>
///     Fingerprint data for ML feature extraction.
///     Populated by fingerprint detector from client-side collection.
/// </summary>
public class FingerprintMetrics
{
    public int? IntegrityScore { get; set; }
    public bool WebGlAnomaly { get; set; }
    public bool CanvasAnomaly { get; set; }
    public bool TimezoneMismatch { get; set; }
    public bool LanguageMismatch { get; set; }
    public bool ScreenResolutionAnomaly { get; set; }
    public int? HeadlessIndicatorCount { get; set; }
}