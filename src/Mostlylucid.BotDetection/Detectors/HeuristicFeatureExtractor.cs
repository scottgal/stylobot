using System.Globalization;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

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
        var features = new Dictionary<string, float>(128, StringComparer.OrdinalIgnoreCase);
        // Snapshot once: DetectionLedger.Contributions calls ToList() on each access.
        var contributions = evidence.Contributions;

        // === Basic Request Metadata ===
        ExtractRequestMetadata(context, features);

        // === Transport Protocol Context ===
        // Critical: tells the model whether this is a document, API, SignalR, WebSocket, etc.
        // Without this, the model penalizes normal API/streaming behavior (missing headers,
        // high velocity, no cookies) as if it were a suspicious page request.
        ExtractTransportContext(evidence, features);

        // === Detector Results (named by actual detector) ===
        ExtractDetectorResults(contributions, features);

        // === Category Breakdown (named by actual category) ===
        ExtractCategoryBreakdown(evidence, features);

        // === Signal Presence (named by actual signal type) ===
        ExtractSignalPresence(evidence, contributions, features);

        // === High-signal structured values (preserve magnitudes, not just presence) ===
        ExtractStructuredSignalValues(evidence, features);

        // === AI/LLM Results (extract actual values, not just presence) ===
        ExtractAiResults(evidence, contributions, features);

        // === Aggregated Statistics ===
        ExtractStatistics(evidence, contributions, features);

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
        var features = new Dictionary<string, float>(32, StringComparer.OrdinalIgnoreCase);
        ExtractRequestMetadata(context, features);
        return features;
    }

    /// <summary>
    ///     Extracts basic request metadata (raw data only, no detection).
    /// </summary>
    private static void ExtractRequestMetadata(HttpContext context, Dictionary<string, float> features)
    {
        var headers = context.Request.Headers;
        // Avoid ToString() — read directly from StringValues to skip string allocation for length check
        var userAgentSv = headers.UserAgent;
        var userAgent = userAgentSv.Count > 0 ? userAgentSv.ToString() : string.Empty;

        // Request basics
        features["req:ua_length"] = Math.Min(userAgent.Length / 200f, 1f);
        features["req:path_length"] = Math.Min((context.Request.Path.Value?.Length ?? 0) / 100f, 1f);
        features["req:query_count"] = Math.Min(context.Request.Query.Count / 10f, 1f);
        features["req:content_length"] = Math.Min((context.Request.ContentLength ?? 0) / 10000f, 1f);
        features["req:is_https"] = context.Request.IsHttps ? 1f : 0f;
        features["req:header_count"] = Math.Min(headers.Count / 20f, 1f);
        features["req:cookie_count"] = Math.Min(context.Request.Cookies.Count / 10f, 1f);

        // Header presence (using header name as feature key)
        var hasAcceptLanguage = headers.ContainsKey("Accept-Language");
        var hasReferer = headers.ContainsKey("Referer");
        features["hdr:accept-language"] = hasAcceptLanguage ? 1f : 0f;
        features["hdr:accept"] = headers.ContainsKey("Accept") ? 1f : 0f;
        features["hdr:referer"] = hasReferer ? 1f : 0f;
        features["hdr:origin"] = headers.ContainsKey("Origin") ? 1f : 0f;
        // Avoid ToString() — compare directly via FirstOrDefault to skip string allocation
        features["hdr:x-requested-with"] = headers["X-Requested-With"].FirstOrDefault()
            ?.Equals("XMLHttpRequest", StringComparison.OrdinalIgnoreCase) == true ? 1f : 0f;
        features["hdr:connection-close"] = headers.Connection.FirstOrDefault()
            ?.Equals("close", StringComparison.OrdinalIgnoreCase) == true ? 1f : 0f;

        // Sec-Fetch-* headers (Fetch Metadata Request Headers, W3C spec)
        // Modern browsers send these on ALL requests to attest origin/mode/destination.
        var secFetchSite = headers["Sec-Fetch-Site"].FirstOrDefault() ?? "";
        var secFetchMode = headers["Sec-Fetch-Mode"].FirstOrDefault() ?? "";
        features["hdr:sec-fetch-site"] = secFetchSite.Length > 0 ? 1f : 0f;
        features["hdr:sec-fetch-mode"] = secFetchMode.Length > 0 ? 1f : 0f;
        var isSameOriginFetch = secFetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase);
        if (isSameOriginFetch) features["hdr:sec_fetch_same_origin"] = 1f;

        // Missing header penalties - absence of expected headers is a bot signal
        // Suppress for same-origin fetch: browser fetch() legitimately omits Accept-Language
        if (!hasAcceptLanguage && !isSameOriginFetch) features["hdr:missing_accept_language"] = 1f;
        if (!hasReferer) features["hdr:missing_referer"] = 1f;

        // User-Agent pattern features — OrdinalIgnoreCase avoids ToLowerInvariant() allocation
        if (userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase)) features["ua:contains_bot"] = 1f;
        if (userAgent.Contains("spider", StringComparison.OrdinalIgnoreCase)) features["ua:contains_spider"] = 1f;
        if (userAgent.Contains("crawler", StringComparison.OrdinalIgnoreCase)) features["ua:contains_crawler"] = 1f;
        if (userAgent.Contains("scraper", StringComparison.OrdinalIgnoreCase)) features["ua:contains_scraper"] = 1f;
        if (userAgent.Contains("headless", StringComparison.OrdinalIgnoreCase)) features["ua:headless"] = 1f;
        if (userAgent.Contains("phantomjs", StringComparison.OrdinalIgnoreCase)) features["ua:phantomjs"] = 1f;
        if (userAgent.Contains("selenium", StringComparison.OrdinalIgnoreCase)) features["ua:selenium"] = 1f;
        if (userAgent.Contains("chrome", StringComparison.OrdinalIgnoreCase)) features["ua:chrome"] = 1f;
        if (userAgent.Contains("firefox", StringComparison.OrdinalIgnoreCase)) features["ua:firefox"] = 1f;
        if (userAgent.Contains("safari", StringComparison.OrdinalIgnoreCase)) features["ua:safari"] = 1f;
        if (userAgent.Contains("edge", StringComparison.OrdinalIgnoreCase)) features["ua:edge"] = 1f;
        if (userAgent.Contains("curl", StringComparison.OrdinalIgnoreCase)) features["ua:curl"] = 1f;
        if (userAgent.Contains("wget", StringComparison.OrdinalIgnoreCase)) features["ua:wget"] = 1f;
        if (userAgent.Contains("python", StringComparison.OrdinalIgnoreCase)) features["ua:python"] = 1f;
        if (userAgent.Contains("scrapy", StringComparison.OrdinalIgnoreCase)) features["ua:scrapy"] = 1f;
        if (userAgent.Contains("requests", StringComparison.OrdinalIgnoreCase)) features["ua:requests"] = 1f;
        if (userAgent.Contains("httpx", StringComparison.OrdinalIgnoreCase)) features["ua:httpx"] = 1f;
        if (userAgent.Contains("aiohttp", StringComparison.OrdinalIgnoreCase)) features["ua:aiohttp"] = 1f;

        // Empty/missing User-Agent - no real browser omits the UA header
        if (userAgent.Length == 0) features["ua:empty"] = 1f;

        // Very short User-Agent (< 15 chars) is suspicious - real browsers have long UAs
        if (userAgent.Length > 0 && userAgent.Length < 15) features["ua:very_short"] = 1f;

        // Detect browser-like UA (Chrome/Firefox/Safari/Edge in the string) — reuse OrdinalIgnoreCase,
        // no extra allocation needed since the individual checks above already computed these
        var isBrowserUa = userAgent.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("firefox", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("safari", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("edge", StringComparison.OrdinalIgnoreCase);

        // Composite: browser UA without typical browser headers = spoofed UA
        // Suppress for same-origin fetch: browser fetch() legitimately omits Accept-Language
        if (isBrowserUa && !hasAcceptLanguage && !isSameOriginFetch) features["combo:browser_no_accept_lang"] = 1f;
        if (isBrowserUa && context.Request.Cookies.Count == 0) features["combo:browser_no_cookies"] = 1f;

        // HTTP method - HEAD is commonly used by scanners/probers
        if (string.Equals(context.Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            features["req:method_head"] = 1f;

        // Path analysis - detect config/env file probing
        // Avoid ToLowerInvariant() — use OrdinalIgnoreCase on raw path value
        var path = context.Request.Path.Value ?? "";
        if (path.Contains("/.env", StringComparison.OrdinalIgnoreCase)) features["path:env_file"] = 1f;
        if (path.StartsWith("/.", StringComparison.OrdinalIgnoreCase) && path.Length > 2) features["path:dotfile"] = 1f;
        if (path.Contains("wp-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("wordpress", StringComparison.OrdinalIgnoreCase)
            || path.Contains("wp-admin", StringComparison.OrdinalIgnoreCase)
            || path.Contains("wp-login", StringComparison.OrdinalIgnoreCase))
            features["path:wordpress_probe"] = 1f;
        if (path.Contains(".git", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".svn", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".hg", StringComparison.OrdinalIgnoreCase))
            features["path:vcs_probe"] = 1f;
        if (path.Contains("config", StringComparison.OrdinalIgnoreCase)
            || path.Contains("backup", StringComparison.OrdinalIgnoreCase)
            || path.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || path.Contains("phpmyadmin", StringComparison.OrdinalIgnoreCase))
            features["path:config_probe"] = 1f;

        // Accept header analysis — use FirstOrDefault to avoid ToString() allocation when possible
        var acceptFirst = headers.Accept.FirstOrDefault() ?? "";
        if (acceptFirst == "*/*") features["accept:wildcard"] = 1f;
        if (acceptFirst.Contains("text/html", StringComparison.OrdinalIgnoreCase)) features["accept:html"] = 1f;
        if (acceptFirst.Contains("application/json", StringComparison.OrdinalIgnoreCase)) features["accept:json"] = 1f;
    }

    /// <summary>
    ///     Extracts transport protocol context from signals written by TransportProtocolContributor.
    ///     This is essential for the model to distinguish document/page requests (where missing
    ///     Accept-Language is suspicious) from API/SignalR/WebSocket traffic (where it's normal).
    /// </summary>
    private static void ExtractTransportContext(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        var signals = evidence.Signals;

        // Core transport type - WebSocket upgrade, SSE, gRPC
        if (signals.TryGetValue(SignalKeys.TransportIsUpgrade, out var upgradeVal) && upgradeVal is true)
            features["transport:is_upgrade"] = 1f;

        // Streaming transport - WebSocket, SSE, SignalR long-polling
        if (signals.TryGetValue(SignalKeys.TransportIsStreaming, out var streamVal) && streamVal is true)
            features["transport:is_streaming"] = 1f;

        // SignalR specifically - negotiate, WebSocket, SSE, long-polling
        if (signals.TryGetValue(SignalKeys.TransportIsSignalR, out var signalrVal) && signalrVal is true)
            features["transport:is_signalr"] = 1f;

        // Protocol class - one-hot encode the major categories
        if (signals.TryGetValue(SignalKeys.TransportProtocolClass, out var classVal) && classVal is string protocolClass)
        {
            var cls = protocolClass.ToLowerInvariant();
            if (cls == "api") features["transport:class_api"] = 1f;
            else if (cls == "signalr") features["transport:class_signalr"] = 1f;
            else if (cls == "grpc") features["transport:class_grpc"] = 1f;
            else if (cls == "document") features["transport:class_document"] = 1f;
            else if (cls == "static") features["transport:class_static"] = 1f;
        }

        // SSE transport
        if (signals.TryGetValue(SignalKeys.TransportSse, out var sseVal) && sseVal is true)
            features["transport:is_sse"] = 1f;
    }

    /// <summary>
    ///     Extracts detector results using actual detector names as feature keys.
    /// </summary>
    private static void ExtractDetectorResults(IReadOnlyList<DetectionContribution> contributions, Dictionary<string, float> features)
    {
        // Single pass: write max-signed and max-abs per detector directly to features.
        // Avoids two GroupBy+ToDictionary allocations and two separate enumerations.
        foreach (var c in contributions)
        {
            var key = NormalizeKey(c.DetectorName);
            var detKey = $"det:{key}";
            var absKey = $"det_abs:{key}";
            var delta = (float)c.ConfidenceDelta;
            var abs = Math.Abs(delta);

            if (!features.TryGetValue(detKey, out var curr) || delta > curr)
                features[detKey] = delta;
            if (!features.TryGetValue(absKey, out var currAbs) || abs > currAbs)
                features[absKey] = abs;
        }
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
    private static void ExtractSignalPresence(AggregatedEvidence evidence, IReadOnlyList<DetectionContribution> contributions, Dictionary<string, float> features)
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
        var hasClientSide = contributions.Any(c =>
            c.DetectorName.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
            c.Category.Equals("ClientSide", StringComparison.OrdinalIgnoreCase));

        var clientSideContrib = contributions
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
    ///     Extracts high-signal structured values so the late heuristic can use actual magnitudes
    ///     from newer detectors instead of only seeing that the signal key happened to exist.
    /// </summary>
    private static void ExtractStructuredSignalValues(AggregatedEvidence evidence, Dictionary<string, float> features)
    {
        var signals = evidence.Signals;

        AddBooleanSignalFeature(signals, features, SignalKeys.HeadersSuspicious, "sigv:headers_suspicious");
        AddBooleanSignalFeature(signals, features, SignalKeys.GeoChangeDriftDetected, "sigv:geo_change_drift");
        AddBooleanSignalFeature(signals, features, SignalKeys.StreamHandshakeStorm, "sigv:stream_handshake_storm");
        AddBooleanSignalFeature(signals, features, SignalKeys.StreamCrossEndpointMixing, "sigv:stream_cross_endpoint_mixing");
        AddBooleanSignalFeature(signals, features, SignalKeys.SimilarityKnownBot, "sigv:similarity_known_bot");
        AddBooleanSignalFeature(signals, features, SignalKeys.AtoDetected, "sigv:ato_detected");

        AddNumericSignalFeature(signals, features, SignalKeys.SimilarityTopScore, "sigv:similarity_top_score");
        AddNumericSignalFeature(signals, features, SignalKeys.CveTopSimilarity, "sigv:cve_top_similarity");
        AddNumericSignalFeature(signals, features, SignalKeys.SessionSelfSimilarity, "sigv:session_self_similarity");
        AddNumericSignalFeature(signals, features, SignalKeys.SessionVelocityMagnitude, "sigv:session_velocity_magnitude");
        AddNumericSignalFeature(signals, features, SignalKeys.SessionVectorMaturity, "sigv:session_vector_maturity");
        AddNumericSignalFeature(signals, features, SignalKeys.IntentThreatScore, "sigv:intent_threat_score");
        AddNumericSignalFeature(signals, features, SignalKeys.GeoCountryBotRate, "sigv:geo_country_bot_rate");
        AddNumericSignalFeature(signals, features, SignalKeys.ClusterAvgSimilarity, "sigv:cluster_avg_similarity");
        AddNumericSignalFeature(signals, features, SignalKeys.ResponseHistoricalScore, "sigv:response_historical_score");
        AddNumericSignalFeature(signals, features, SignalKeys.WaveformTimingRegularity, "sigv:waveform_timing_regularity");
        AddNumericSignalFeature(signals, features, SignalKeys.WaveformPathDiversity, "sigv:waveform_path_diversity");
        AddNumericSignalFeature(signals, features, SignalKeys.AtoDriftScore, "sigv:ato_drift_score");

        AddNormalizedCountFeature(signals, features, SignalKeys.ResponseCount404, "sigv:response_404_count", 20f);
        AddNormalizedCountFeature(signals, features, SignalKeys.ResponseUnique404Paths, "sigv:response_unique_404_paths", 10f);
        AddNormalizedCountFeature(signals, features, SignalKeys.ResponseHoneypotHits, "sigv:response_honeypot_hits", 5f);
        AddNormalizedCountFeature(signals, features, SignalKeys.ResponseAuthFailures, "sigv:response_auth_failures", 20f);
        AddNormalizedCountFeature(signals, features, SignalKeys.ResponseRateLimitViolations, "sigv:response_rate_limit_violations", 10f);
        AddNormalizedCountFeature(signals, features, SignalKeys.SimilarityMatchCount, "sigv:similarity_match_count", 5f);
        AddNormalizedCountFeature(signals, features, SignalKeys.CveMatchCount, "sigv:cve_match_count", 5f);
        AddNormalizedCountFeature(signals, features, SignalKeys.SessionRequestCount, "sigv:session_request_count", 20f);
        AddNormalizedCountFeature(signals, features, SignalKeys.SessionHistoryCount, "sigv:session_history_count", 10f);
        AddNormalizedCountFeature(signals, features, SignalKeys.StreamConcurrentStreams, "sigv:stream_concurrent_streams", 10f);
        AddNormalizedCountFeature(signals, features, SignalKeys.StreamReconnectRate, "sigv:stream_reconnect_rate", 60f);

        AddStringEnumFeature(signals, features, SignalKeys.IntentThreatBand, "sigv:intent_band");
        AddStringEnumFeature(signals, features, SignalKeys.CveTopSeverity, "sigv:cve_severity");

        // Click fraud signals
        AddNumericSignalFeature(signals, features, SignalKeys.ClickFraudConfidence, "cf:click_fraud_score");
        AddBooleanSignalFeature(signals, features, SignalKeys.ClickFraudIsPaidTraffic, "cf:is_paid_traffic");
    }

    /// <summary>
    ///     Extracts AI/LLM detector results as numeric features.
    ///     This provides the actual prediction values, not just presence indicators.
    ///     Critical for late heuristic to incorporate AI feedback.
    /// </summary>
    private static void ExtractAiResults(AggregatedEvidence evidence, IReadOnlyList<DetectionContribution> contributions, Dictionary<string, float> features)
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
        var llmContribution = contributions
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
    private static void ExtractStatistics(AggregatedEvidence evidence, IReadOnlyList<DetectionContribution> contributions, Dictionary<string, float> features)
    {
        // Per-detector max-absolute score (one pass, no LINQ materialization).
        var detMaxAbs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in contributions)
        {
            var abs = Math.Abs(c.ConfidenceDelta);
            if (!detMaxAbs.TryGetValue(c.DetectorName, out var cur) || abs > cur)
                detMaxAbs[c.DetectorName] = abs;
        }

        var detCount = detMaxAbs.Count;
        var detFlagged = 0;
        var detMax = 0.0;
        var detSum = 0.0;
        foreach (var score in detMaxAbs.Values)
        {
            if (score > 0.3) detFlagged++;
            if (score > detMax) detMax = score;
            detSum += score;
        }
        var detAvg = detCount > 0 ? detSum / detCount : 0.0;

        // Inline variance (two-pass over the small dictionary values).
        var detVar = 0.0;
        if (detCount > 1)
        {
            foreach (var score in detMaxAbs.Values)
                detVar += (score - detAvg) * (score - detAvg);
            detVar /= detCount;
        }

        features["stat:detector_count"] = Math.Min(detCount / 10f, 1f);
        features["stat:detector_flagged"] = Math.Min(detFlagged / 6f, 1f);
        features["stat:detector_max"] = (float)detMax;
        features["stat:detector_avg"] = (float)detAvg;
        features["stat:detector_variance"] = (float)detVar;

        // Category stats: direct iteration, no Select+ToList.
        var catCount = evidence.CategoryBreakdown.Count;
        var catMax = 0.0;
        var catSum = 0.0;
        foreach (var breakdown in evidence.CategoryBreakdown.Values)
        {
            if (breakdown.Score > catMax) catMax = breakdown.Score;
            catSum += breakdown.Score;
        }
        features["stat:category_count"] = Math.Min(catCount / 8f, 1f);
        features["stat:category_max"] = (float)catMax;
        features["stat:category_avg"] = catCount > 0 ? (float)(catSum / catCount) : 0f;

        features["stat:contribution_count"] = Math.Min(contributions.Count / 20f, 1f);
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

    private static void AddBooleanSignalFeature(
        IReadOnlyDictionary<string, object> signals,
        Dictionary<string, float> features,
        string signalKey,
        string featureKey)
    {
        if (!signals.TryGetValue(signalKey, out var value))
            return;

        var normalized = value switch
        {
            bool b => b ? 1f : 0f,
            string s when bool.TryParse(s, out var parsed) => parsed ? 1f : 0f,
            _ => 0f
        };

        features[featureKey] = normalized;
    }

    private static void AddNumericSignalFeature(
        IReadOnlyDictionary<string, object> signals,
        Dictionary<string, float> features,
        string signalKey,
        string featureKey)
    {
        if (!TryGetNumericSignalValue(signals, signalKey, out var value))
            return;

        features[featureKey] = Math.Clamp((float)value, 0f, 1f);
    }

    private static void AddNormalizedCountFeature(
        IReadOnlyDictionary<string, object> signals,
        Dictionary<string, float> features,
        string signalKey,
        string featureKey,
        float scale)
    {
        if (!TryGetNumericSignalValue(signals, signalKey, out var value) || scale <= 0f)
            return;

        features[featureKey] = Math.Clamp((float)value / scale, 0f, 1f);
    }

    private static void AddStringEnumFeature(
        IReadOnlyDictionary<string, object> signals,
        Dictionary<string, float> features,
        string signalKey,
        string featurePrefix)
    {
        if (!signals.TryGetValue(signalKey, out var value))
            return;

        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        features[$"{featurePrefix}:{NormalizeKey(text)}"] = 1f;
    }

    private static bool TryGetNumericSignalValue(
        IReadOnlyDictionary<string, object> signals,
        string signalKey,
        out double value)
    {
        value = 0.0;
        if (!signals.TryGetValue(signalKey, out var rawValue))
            return false;

        switch (rawValue)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
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
