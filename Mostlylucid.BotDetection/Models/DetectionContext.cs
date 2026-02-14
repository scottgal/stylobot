using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Detectors;

namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Shared context bus for detection pipeline.
///     Allows detectors to share signals and read results from earlier stages.
/// </summary>
public class DetectionContext
{
    private readonly ConcurrentDictionary<string, DetectorResult> _detectorResults = new();
    private readonly ConcurrentBag<LearnedSignal> _learnings = new();
    private readonly ConcurrentBag<DetectionReason> _reasons = new();
    private readonly ConcurrentDictionary<string, double> _scores = new();
    private readonly ConcurrentDictionary<string, object> _signals = new();

    /// <summary>
    ///     The HTTP context being analyzed
    /// </summary>
    public required HttpContext HttpContext { get; init; }

    /// <summary>
    ///     Cancellation token for the detection pipeline
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    #region Signal Bus

    /// <summary>
    ///     Set a signal value for other detectors to read
    /// </summary>
    public void SetSignal<T>(string key, T value) where T : notnull
    {
        _signals[key] = value;
    }

    /// <summary>
    ///     Get a signal value from an earlier detector
    /// </summary>
    public T? GetSignal<T>(string key)
    {
        if (_signals.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    /// <summary>
    ///     Check if a signal exists
    /// </summary>
    public bool HasSignal(string key)
    {
        return _signals.ContainsKey(key);
    }

    /// <summary>
    ///     Get all signal keys
    /// </summary>
    public IEnumerable<string> SignalKeys => _signals.Keys;

    #endregion

    #region Score Aggregation

    /// <summary>
    ///     Record a score from a detector
    /// </summary>
    public void SetScore(string detectorName, double score)
    {
        _scores[detectorName] = score;
    }

    /// <summary>
    ///     Get a specific detector's score
    /// </summary>
    public double? GetScore(string detectorName)
    {
        return _scores.TryGetValue(detectorName, out var score) ? score : null;
    }

    /// <summary>
    ///     Get all scores
    /// </summary>
    public IReadOnlyDictionary<string, double> Scores => _scores;

    /// <summary>
    ///     Get the maximum score from all detectors so far
    /// </summary>
    public double MaxScore => _scores.Values.DefaultIfEmpty(0).Max();

    /// <summary>
    ///     Get the average score from all detectors so far
    /// </summary>
    public double AverageScore => _scores.Values.DefaultIfEmpty(0).Average();

    #endregion

    #region Reason Accumulation

    /// <summary>
    ///     Add a detection reason
    /// </summary>
    public void AddReason(DetectionReason reason)
    {
        _reasons.Add(reason);
    }

    /// <summary>
    ///     Add multiple detection reasons
    /// </summary>
    public void AddReasons(IEnumerable<DetectionReason> reasons)
    {
        foreach (var reason in reasons)
            _reasons.Add(reason);
    }

    /// <summary>
    ///     Get all accumulated reasons
    /// </summary>
    public IReadOnlyList<DetectionReason> Reasons => _reasons.ToList();

    #endregion

    #region Detector Results

    /// <summary>
    ///     Store a detector's full result
    /// </summary>
    public void SetDetectorResult(string detectorName, DetectorResult result)
    {
        _detectorResults[detectorName] = result;
    }

    /// <summary>
    ///     Get a specific detector's result
    /// </summary>
    public DetectorResult? GetDetectorResult(string detectorName)
    {
        return _detectorResults.TryGetValue(detectorName, out var result) ? result : null;
    }

    /// <summary>
    ///     Get all detector results
    /// </summary>
    public IReadOnlyDictionary<string, DetectorResult> DetectorResults => _detectorResults;

    #endregion

    #region Learning Signals

    /// <summary>
    ///     Record a signal that should be fed back to ML for learning
    /// </summary>
    public void AddLearning(LearnedSignal signal)
    {
        _learnings.Add(signal);
    }

    /// <summary>
    ///     Get all learning signals
    /// </summary>
    public IReadOnlyList<LearnedSignal> Learnings => _learnings.ToList();

    #endregion
}

/// <summary>
///     A signal captured for ML feedback/learning
/// </summary>
public class LearnedSignal
{
    /// <summary>
    ///     Which detector generated this signal
    /// </summary>
    public required string SourceDetector { get; init; }

    /// <summary>
    ///     Type of signal (e.g., "Pattern", "Anomaly", "Inconsistency")
    /// </summary>
    public required string SignalType { get; init; }

    /// <summary>
    ///     The signal value/pattern
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    ///     Confidence in this signal
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    ///     Additional metadata
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
///     Well-known signal keys for cross-detector communication.
///     This is a partial class - additional keys may be defined in other files.
/// </summary>
public static class SignalKeys
{
    // Stage 0 signals (raw detection)
    public const string UserAgent = "ua.raw";
    public const string UserAgentIsBot = "ua.is_bot";
    public const string UserAgentBotType = "ua.bot_type";
    public const string UserAgentBotName = "ua.bot_name";

    public const string HeadersPresent = "headers.present";
    public const string HeadersMissing = "headers.missing";
    public const string HeadersSuspicious = "headers.suspicious";

    public const string ClientIp = "ip.address";
    public const string IpIsDatacenter = "ip.is_datacenter";
    public const string IpIsLocal = "ip.is_local";
    public const string IpProvider = "ip.provider";

    public const string FingerprintHash = "fingerprint.hash";
    public const string FingerprintHeadlessScore = "fingerprint.headless_score";
    public const string FingerprintIntegrityScore = "fingerprint.integrity_score";

    // Stage 1 signals (behavioral)
    public const string BehavioralRateExceeded = "behavioral.rate_exceeded";
    public const string BehavioralAnomalyDetected = "behavioral.anomaly";
    public const string BehavioralRequestCount = "behavioral.request_count";

    // Stage 1 signals (version age)
    public const string VersionAgeAnalyzed = "versionage.analyzed";
    public const string BrowserVersionAge = "versionage.browser_age";
    public const string OsVersionAge = "versionage.os_age";

    // Stage 2 signals (meta-layers)
    public const string InconsistencyScore = "inconsistency.score";
    public const string InconsistencyDetails = "inconsistency.details";

    public const string RiskBand = "risk.band";
    public const string RiskScore = "risk.score";

    // AI/LLM signals
    public const string AiPrediction = "ai.prediction";
    public const string AiConfidence = "ai.confidence";
    public const string AiLearnedPattern = "ai.learned_pattern";

    // Heuristic signals (meta-layer that consumes all evidence)
    public const string HeuristicPrediction = "heuristic.prediction";
    public const string HeuristicConfidence = "heuristic.confidence";
    public const string HeuristicEarlyCompleted = "heuristic.early_completed";

    // ==========================================
    // Security tool detection signals
    // Set by SecurityToolContributor when penetration testing tools are detected
    // ==========================================

    /// <summary>Boolean: true if a security/hacking tool was detected in User-Agent</summary>
    public const string SecurityToolDetected = "security.tool_detected";

    /// <summary>String: Name of the detected security tool (e.g., "SQLMap", "Nikto")</summary>
    public const string SecurityToolName = "security.tool_name";

    /// <summary>String: Category of the security tool (e.g., "SqlInjection", "VulnerabilityScanner")</summary>
    public const string SecurityToolCategory = "security.tool_category";

    // ==========================================
    // Project Honeypot signals
    // Set by ProjectHoneypotContributor after HTTP:BL DNS lookup
    // ==========================================

    /// <summary>Boolean: true if Project Honeypot lookup was performed</summary>
    public const string HoneypotChecked = "honeypot.checked";

    /// <summary>Boolean: true if IP is listed in Project Honeypot database</summary>
    public const string HoneypotListed = "honeypot.listed";

    /// <summary>Int: Threat score from 0-255 (higher = more dangerous)</summary>
    public const string HoneypotThreatScore = "honeypot.threat_score";

    /// <summary>String: Visitor type flags (Suspicious, Harvester, CommentSpammer, SearchEngine)</summary>
    public const string HoneypotVisitorType = "honeypot.visitor_type";

    /// <summary>Int: Days since the IP was last seen in a honeypot trap</summary>
    public const string HoneypotDaysSinceLastActivity = "honeypot.days_since_activity";

    // ==========================================
    // Reputation bias signals
    // Set by ReputationBiasContributor when learned patterns provide initial bias
    // ==========================================

    /// <summary>Boolean: true if reputation bias was applied from learned patterns</summary>
    public const string ReputationBiasApplied = "reputation.bias_applied";

    /// <summary>Int: number of reputation patterns that matched</summary>
    public const string ReputationBiasCount = "reputation.bias_count";

    /// <summary>Boolean: true if any matched pattern can trigger fast abort (known bad)</summary>
    public const string ReputationCanAbort = "reputation.can_abort";

    /// <summary>Boolean: true if any matched pattern can trigger fast allow (known good)</summary>
    public const string ReputationCanAllow = "reputation.can_allow";

    /// <summary>Boolean: true if fast-path reputation check found a confirmed pattern (good or bad)</summary>
    public const string ReputationFastPathHit = "reputation.fastpath_hit";

    // ==========================================
    // TimescaleDB historical reputation signals
    // Set by TimescaleReputationContributor when querying 90-day history
    // ==========================================

    /// <summary>Double: historical bot-to-total ratio (0.0-1.0)</summary>
    public const string TsBotRatio = "ts.bot_ratio";

    /// <summary>Int: total historical hit count</summary>
    public const string TsHitCount = "ts.hit_count";

    /// <summary>Int: number of distinct days the signature has been active</summary>
    public const string TsDaysActive = "ts.days_active";

    /// <summary>Int: number of requests in the last hour (velocity)</summary>
    public const string TsVelocity = "ts.velocity";

    /// <summary>Boolean: true if no historical data exists for this signature</summary>
    public const string TsIsNew = "ts.is_new";

    /// <summary>Boolean: true if historical data is conclusive enough to skip LLM</summary>
    public const string TsIsConclusive = "ts.is_conclusive";

    /// <summary>Double: average bot probability across all historical observations</summary>
    public const string TsAvgBotProb = "ts.avg_bot_prob";

    // ==========================================
    // Cache behavior signals
    // Set by CacheBehaviorContributor when analyzing caching patterns
    // ==========================================

    /// <summary>Boolean: true if cache validation headers (If-None-Match, If-Modified-Since) are missing</summary>
    public const string CacheValidationMissing = "cache.validation_missing";

    /// <summary>Boolean: true if client supports compression (gzip, br)</summary>
    public const string CompressionSupported = "cache.compression_supported";

    /// <summary>Boolean: true if rapid repeated requests for same resource detected</summary>
    public const string RapidRepeatedRequest = "cache.rapid_repeated";

    /// <summary>Boolean: true if overall cache behavior patterns are anomalous</summary>
    public const string CacheBehaviorAnomaly = "cache.behavior_anomaly";

    // ==========================================
    // TCP/IP fingerprinting signals
    // Set by TcpIpFingerprintContributor
    // ==========================================

    /// <summary>String: OS hint from TCP/IP fingerprint analysis</summary>
    public const string TcpOsHint = "tcp.os_hint";

    /// <summary>String: OS hint derived from TTL value</summary>
    public const string TcpOsHintTtl = "tcp.os_hint_ttl";

    /// <summary>String: OS hint derived from TCP window size</summary>
    public const string TcpOsHintWindow = "tcp.os_hint_window";

    // ==========================================
    // TLS fingerprinting signals
    // Set by TlsFingerprintContributor
    // ==========================================

    /// <summary>String: TLS protocol version (e.g., TLSv1.2, TLSv1.3)</summary>
    public const string TlsProtocol = "tls.protocol";

    // ==========================================
    // HTTP/2 fingerprinting signals
    // Set by Http2FingerprintContributor
    // ==========================================

    /// <summary>String: HTTP protocol version (e.g., HTTP/2, HTTP/1.1)</summary>
    public const string H2Protocol = "h2.protocol";

    /// <summary>String: Client type inferred from HTTP/2 fingerprint</summary>
    public const string H2ClientType = "h2.client_type";

    // ==========================================
    // HTTP/3 (QUIC) fingerprinting signals
    // Set by Http3FingerprintContributor
    // ==========================================

    /// <summary>String: HTTP/3 protocol version</summary>
    public const string H3Protocol = "h3.protocol";

    /// <summary>String: Client type inferred from QUIC transport parameters</summary>
    public const string H3ClientType = "h3.client_type";

    /// <summary>Boolean: Whether QUIC 0-RTT resumption was used (returning visitor)</summary>
    public const string H3ZeroRtt = "h3.zero_rtt";

    /// <summary>Boolean: Whether QUIC connection migration occurred (mobile user)</summary>
    public const string H3ConnectionMigrated = "h3.connection_migrated";

    // ==========================================
    // User-Agent parsed signals
    // Used by MultiLayerCorrelationContributor
    // ==========================================

    /// <summary>String: Parsed OS from User-Agent</summary>
    public const string UserAgentOs = "user_agent.os";

    /// <summary>String: Parsed browser from User-Agent</summary>
    public const string UserAgentBrowser = "user_agent.browser";

    // ==========================================
    // Correlation signals
    // Set by MultiLayerCorrelationContributor
    // ==========================================

    /// <summary>Double: Cross-layer consistency score (0.0 = all mismatched, 1.0 = all consistent)</summary>
    public const string CorrelationConsistencyScore = "correlation.consistency_score";

    /// <summary>Int: Number of cross-layer anomalies detected</summary>
    public const string CorrelationAnomalyCount = "correlation.anomaly_count";

    /// <summary>Boolean: OS mismatch between TCP fingerprint and User-Agent</summary>
    public const string CorrelationOsMismatch = "correlation.os_mismatch";

    /// <summary>Boolean: Browser mismatch between HTTP/2 fingerprint and User-Agent</summary>
    public const string CorrelationBrowserMismatch = "correlation.browser_mismatch";

    // ==========================================
    // Waveform signals
    // Set by BehavioralWaveformContributor
    // ==========================================

    /// <summary>String: Client waveform signature (IP:UA hash)</summary>
    public const string WaveformSignature = "waveform.signature";

    /// <summary>Double: Timing regularity score (coefficient of variation)</summary>
    public const string WaveformTimingRegularity = "waveform.timing_regularity_score";

    /// <summary>Boolean: Whether a request burst was detected</summary>
    public const string WaveformBurstDetected = "waveform.burst_detected";

    /// <summary>Double: Path diversity ratio (unique paths / total paths)</summary>
    public const string WaveformPathDiversity = "waveform.path_diversity";

    // ==========================================
    // Client interaction signals
    // Set by client-side JavaScript tracking
    // ==========================================

    /// <summary>Int: Number of mouse events detected</summary>
    public const string ClientMouseEvents = "client.mouse_events";

    /// <summary>Int: Number of keyboard events detected</summary>
    public const string ClientKeyboardEvents = "client.keyboard_events";

    // ==========================================
    // Similarity search signals
    // Set by SimilarityContributor
    // ==========================================

    /// <summary>Float: Highest similarity score to known signatures</summary>
    public const string SimilarityTopScore = "similarity.top_score";

    /// <summary>Int: Number of similar signatures found above threshold</summary>
    public const string SimilarityMatchCount = "similarity.match_count";

    /// <summary>Boolean: Whether the most similar signature was a known bot</summary>
    public const string SimilarityKnownBot = "similarity.known_bot";

    // ==========================================
    // AI scraper detection signals
    // Set by AiScraperContributor
    // ==========================================

    /// <summary>Boolean: true if a known AI scraper/crawler was detected</summary>
    public const string AiScraperDetected = "aiscraper.detected";

    /// <summary>String: Name of the detected AI bot (e.g., "GPTBot", "ClaudeBot")</summary>
    public const string AiScraperName = "aiscraper.name";

    /// <summary>String: Operator of the AI bot (e.g., "OpenAI", "Anthropic")</summary>
    public const string AiScraperOperator = "aiscraper.operator";

    /// <summary>String: Category of the AI bot (Training, Search, Assistant, ScrapingService)</summary>
    public const string AiScraperCategory = "aiscraper.category";
}