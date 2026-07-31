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
public static partial class SignalKeys
{
    // Stage 0 signals (raw detection)

    /// <summary>String: raw User-Agent header value, verbatim from the request.</summary>
    public const string UserAgent = "ua.raw";

    /// <summary>Bool: true when the UA matches a known automation / bot pattern (UserAgentContributor classification).</summary>
    public const string UserAgentIsBot = "ua.is_bot";

    /// <summary>String: bot category when ua.is_bot is true (e.g. SearchEngine, Scraper, Monitoring).</summary>
    public const string UserAgentBotType = "ua.bot_type";

    /// <summary>String: bot product name when ua.is_bot is true (e.g. Googlebot, MJ12bot, AhrefsBot).</summary>
    public const string UserAgentBotName = "ua.bot_name";

    /// <summary>
    ///     String: the per-instance discriminator extracted from a UA's
    ///     <c>+URL</c> comment marker. For fediverse link-preview bots
    ///     (Mastodon, Pleroma, Akkoma, Misskey, etc.) this is the
    ///     instance hostname (<c>mastodon.social</c>, <c>mas.to</c>).
    ///     For self-identifying crawlers it's the home URL host when the
    ///     URL is NOT in the vendor-home skiplist. Absent / empty when
    ///     the UA carries no discriminator or the URL resolved to a
    ///     vendor-home reference (<c>openai.com</c> for GPTBot,
    ///     <c>www.google.com</c> for Googlebot).
    ///     <para>
    ///         Use this for per-instance trust accumulation, signature
    ///         disambiguation, and dashboard display. Do NOT compose
    ///         display names from this alone; pair with
    ///         <see cref="UserAgentBotName"/>. Written by
    ///         <c>UserAgentContributor</c> via
    ///         <see cref="Helpers.UserAgentDiscriminator.ExtractDiscriminator"/>.
    ///     </para>
    /// </summary>
    public const string UserAgentBotInstance = "ua.bot_instance";

    /// <summary>String: parsed browser/agent family from the UA (e.g. Chrome, Firefox, Safari, curl, python-requests).</summary>
    public const string UserAgentFamily = "ua.family";

    /// <summary>String: parsed family + major version from the UA (e.g. "Chrome 138", "Safari 17").</summary>
    public const string UserAgentFamilyVersion = "ua.family_version";

    /// <summary>Bool: at least one expected browser header was absent (Accept, Accept-Language, Accept-Encoding, etc.).</summary>
    public const string HeadersMissing = "headers.missing";

    /// <summary>Bool: header set carried at least one bot-like indicator (Phantom-JS markers, headless tells, etc.).</summary>
    public const string HeadersSuspicious = "headers.suspicious";

    // Sec-Fetch-* headers (W3C Fetch Metadata Request Headers)
    // Set by HeaderContributor; consumed by InconsistencyContributor, HeuristicFeatureExtractor

    /// <summary>String: value of Sec-Fetch-Site header (e.g., "same-origin", "cross-site")</summary>
    public const string HeaderSecFetchSite = "header.sec_fetch_site";

    /// <summary>String: value of Sec-Fetch-Mode header (e.g., "cors", "navigate")</summary>
    public const string HeaderSecFetchMode = "header.sec_fetch_mode";

    /// <summary>String: value of Sec-Fetch-Dest header (e.g., "empty", "document")</summary>
    public const string HeaderSecFetchDest = "header.sec_fetch_dest";

    /// <summary>Boolean: true if Sec-Fetch-Site is "same-origin" (browser attestation of programmatic fetch)</summary>
    public const string HeaderSecFetchSameOrigin = "header.sec_fetch_same_origin";

    // ----- Per-request header shape signals (HeaderContributor) -----

    /// <summary>Int: total number of HTTP headers on the inbound request.</summary>
    public const string HeaderCount = "header.count";

    /// <summary>Bool: true when the request carries an Accept header.</summary>
    public const string HeaderHasAccept = "header.has_accept";

    /// <summary>Bool: true when the request carries an Accept-Encoding header.</summary>
    public const string HeaderHasAcceptEncoding = "header.has_accept_encoding";

    /// <summary>Bool: true when the request carries an Accept-Language header.</summary>
    public const string HeaderHasAcceptLanguage = "header.has_accept_language";

    /// <summary>Bool: true when the request carries proxy headers (X-Forwarded-For or Via).</summary>
    public const string HeaderHasProxyHeaders = "header.has_proxy_headers";

    /// <summary>Bool: true when the request looks like a Service Worker registration fetch (Service-Worker: script).</summary>
    public const string HeaderIsServiceWorkerFetch = "header.is_service_worker_fetch";

    /// <summary>Bool: true when the request is a WebSocket upgrade (RFC 6455) and the omitted Accept-* headers should not be penalised.</summary>
    public const string HeaderIsWebSocketUpgrade = "header.is_websocket_upgrade";

    /// <summary>Double in [0,1]: rolling fraction of recent requests in the same UA bucket that carried an Accept header (deployment norm).</summary>
    public const string HeaderPopulationAcceptRate = "header.population_accept_rate";

    /// <summary>Double in [0,1]: rolling fraction of recent requests in the same UA bucket that carried an Accept-Language header (deployment norm).</summary>
    public const string HeaderPopulationAcceptLanguageRate = "header.population_accept_language_rate";

    // ----- Header correlation (HeaderCorrelationContributor) -----

    /// <summary>Int: number of distinct primary signatures the gateway has seen reuse this header fingerprint (template-reuse detector).</summary>
    public const string HeaderCorrelationDistinctSignatures = "header_correlation.distinct_signatures";

    /// <summary>String: short hash of the request's full header shape, used to find clients reusing the same header template across rotating IPs/UAs.</summary>
    public const string HeaderCorrelationHeaderFingerprint = "header_correlation.header_fingerprint";

    // Programmatic request attestation - signals that a request is a legitimate
    // programmatic call (browser fetch, API client with key, SignalR) rather than
    // a scraping bot. Downstream detectors use this to downweight false-positive
    // signals like missing cookies, missing referer, regular timing, etc.
    // Based on W3C Fetch Metadata, API key presence, and request context.

    /// <summary>Boolean: true if request has browser fetch attestation (Sec-Fetch-Site present)</summary>
    public const string ProgrammaticFetchAttestation = "attestation.fetch_metadata";

    /// <summary>Boolean: true if request carries a valid API key</summary>
    public const string ProgrammaticApiKey = "attestation.api_key";

    /// <summary>Boolean: composite - true if ANY programmatic attestation signal is present</summary>
    public const string ProgrammaticRequest = "attestation.programmatic";

    /// <summary>String: client IP address as resolved by the proxy/forwarded-headers chain.</summary>
    public const string ClientIp = "ip.address";

    /// <summary>Bool: true when the client IP resolves to a known datacenter / cloud provider ASN.</summary>
    public const string IpIsDatacenter = "ip.is_datacenter";

    /// <summary>Bool: true when the client IP is RFC1918 / loopback / link-local (test or internal traffic).</summary>
    public const string IpIsLocal = "ip.is_local";

    /// <summary>
    ///     Peer-verified trust for the Internal (LAN -> logonly) enforcement carve-out. Set from
    ///     the real TCP peer (Connection.RemoteIpAddress) + InternalTrust config, NEVER from
    ///     X-Forwarded-For. Distinct from <see cref="IpIsLocal"/> (a detection feature computed
    ///     from the resolved client IP, which may be header-derived and is therefore unsafe as a
    ///     bypass gate). The Internal classification reads THIS signal, not IpIsLocal.
    /// </summary>
    public const string IpIsTrustedInternal = "ip.is_trusted_internal";

    /// <summary>String: hosting provider name when the IP resolves to a known cloud / VPS / hosting ASN.</summary>
    public const string IpProvider = "ip.provider";

    /// <summary>Number: AS number of the network owning the client IP.</summary>
    public const string IpAsn = "ip.asn";

    /// <summary>String: AS organization name for the client IP's ASN.</summary>
    public const string IpAsnOrg = "ip.asn_org";

    /// <summary>Bool: true when the client IP is an IPv6 address.</summary>
    public const string IpIsIpv6 = "ip.is_ipv6";

    /// <summary>String: inferred proxy topology hint (e.g. "direct", "cdn", "proxy_chain") from forwarded headers and IP characteristics.</summary>
    public const string ProxyTopology = "proxy.topology";

    /// <summary>Double in [0,1]: composite "looks-headless" score derived from clientside fingerprint signals.</summary>
    public const string FingerprintHeadlessScore = "fingerprint.headless_score";

    /// <summary>Double in [0,1]: composite fingerprint-integrity score (how consistent the fingerprint is across factors).</summary>
    public const string FingerprintIntegrityScore = "fingerprint.integrity_score";

    // Stage 1 signals (behavioral)

    /// <summary>Bool: true when the per-client request rate exceeded the configured behavioural threshold.</summary>
    public const string BehavioralRateExceeded = "behavioral.rate_exceeded";

    /// <summary>Bool: true when behavioural-waveform analysis detected an anomaly (regular timing, lockstep cadence, etc.).</summary>
    public const string BehavioralAnomalyDetected = "behavioral.anomaly";

    // Stage 1 signals (version age)

    /// <summary>Bool: true once VersionAgeContributor has finished its analysis (downstream gating signal).</summary>
    public const string VersionAgeAnalyzed = "versionage.analyzed";

    /// <summary>Int: estimated age in days of the browser version inferred from the UA.</summary>
    public const string BrowserVersionAge = "versionage.browser_age";

    // Stage 2 signals (meta-layers)

    /// <summary>Double in [0,1]: composite cross-layer inconsistency score (e.g. UA vs TLS vs TCP).</summary>
    public const string InconsistencyScore = "inconsistency.score";

    /// <summary>String: human-readable breakdown of which layers disagreed and how.</summary>
    public const string InconsistencyDetails = "inconsistency.details";

    /// <summary>String: final risk band assigned to this request (Low / Medium / High / VeryHigh).</summary>
    public const string RiskBand = "risk.band";

    /// <summary>Double in [0,1]: final aggregated bot-probability score for this request.</summary>
    public const string RiskScore = "risk.score";

    /// <summary>String: human-readable explanation of why this risk band was assigned</summary>
    public const string RiskJustification = "risk.justification";

    /// <summary>String: trace of the friendly-bot pin evaluation. Format is
    /// "fired:&lt;source&gt;" / "skipped:&lt;reason&gt;" / "not-applicable:&lt;reason&gt;"
    /// so the dashboard can show why a known-friendly bot (MJ12bot, AhrefsBot,
    /// DuckDuckBot) ended up VeryHigh or, conversely, why a spoofed UA managed to
    /// pin Low. Always set by DetermineRiskBand.</summary>
    public const string RiskFriendlyPinTrace = "risk.friendly_pin_trace";

    /// <summary>Bool: set by a vendor-IP-verifying contributor (Commercial:
    /// GoodBotIpRangeIndex / reverse-DNS verifier) when a UA classified as a
    /// friendly bot (Googlebot, Bingbot, MJ12bot, etc.) also matches the
    /// published vendor IP ranges. False means the UA looked friendly but the
    /// client IP did not verify and the friendly pin must NOT fire (likely
    /// spoofed UA). Absent (null) means no verification was attempted, in which
    /// case the friendly pin falls back to UA-only behaviour (FOSS default).</summary>
    public const string FriendlyIpVerified = "friendly.ip_verified";

    /// <summary>Bool: set by FediverseDomainContributor when a UA matching the
    /// canonical fediverse pattern (Mastodon, Pleroma, Misskey, etc., where UA
    /// contains "+https://instance/") is corroborated by a successful NodeInfo
    /// lookup against that instance. NodeInfo is the ActivityPub-spec mechanism
    /// for proving an instance domain hosts real fediverse software -- the
    /// protocol-defined cross-corroboration signal for traffic that cannot be
    /// IP-range verified (instances run on arbitrary cloud IPs).
    ///
    /// Semantics mirror FriendlyIpVerified: true = NodeInfo confirmed, false =
    /// lookup ran and failed (likely spoofed UA), null = no verification
    /// attempted. Treated as a parallel corroborating signal: when EITHER
    /// FriendlyIpVerified or FriendlyDomainVerified is true the friendly pin
    /// fires; either being false still blocks.</summary>
    public const string FriendlyDomainVerified = "friendly.domain_verified";

    // AI/LLM signals

    /// <summary>String: LLM escalation verdict (e.g. "Human", "Bot", "Unknown") when AI inspection ran.</summary>
    public const string AiPrediction = "ai.prediction";

    /// <summary>Double in [0,1]: confidence reported by the LLM escalation for the verdict in <see cref="AiPrediction"/>.</summary>
    public const string AiConfidence = "ai.confidence";

    /// <summary>String: learned pattern identifier the LLM matched against (when AI inspection escalated to pattern-similarity).</summary>
    public const string AiLearnedPattern = "ai.learned_pattern";

    // Heuristic signals (meta-layer that consumes all evidence)

    /// <summary>String: early heuristic prediction (e.g. "Human", "Bot", "Unknown") emitted before late detectors run.</summary>
    public const string HeuristicPrediction = "heuristic.prediction";

    /// <summary>Double in [0,1]: confidence of the early heuristic prediction.</summary>
    public const string HeuristicConfidence = "heuristic.confidence";

    /// <summary>Bool: true when the early heuristic was confident enough to short-circuit subsequent detectors.</summary>
    public const string HeuristicEarlyCompleted = "heuristic.early_completed";

    // Late heuristic signals (runs after all detectors, uses full evidence)

    /// <summary>String: late heuristic prediction emitted after the full evidence ledger is complete.</summary>
    public const string HeuristicLatePrediction = "heuristic.late_prediction";

    /// <summary>Double in [0,1]: confidence of the late heuristic prediction.</summary>
    public const string HeuristicLateConfidence = "heuristic.late_confidence";

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

    /// <summary>Boolean: true if fast-path reputation attempted a fast-abort (may be downgraded by browser attestation)</summary>
    public const string ReputationFastAbortActive = "reputation.fast_abort_active";

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

    /// <summary>String: value of the HTTP Connection header (e.g. "keep-alive", "close", "Upgrade") as observed on the inbound socket.</summary>
    public const string TcpConnectionHeader = "tcp.connection_header";

    // ==========================================
    // TLS fingerprinting signals
    // Set by TlsFingerprintContributor
    // ==========================================

    /// <summary>String: TLS protocol version (e.g., TLSv1.2, TLSv1.3)</summary>
    public const string TlsProtocol = "tls.protocol";

    /// <summary>Bool: true when TLS handshake detail was available for this request (some edge hops strip it).</summary>
    public const string TlsAvailable = "tls.available";

    /// <summary>Bool: true when the request arrived over HTTPS.</summary>
    public const string TlsIsHttps = "tls.is_https";

    // ==========================================
    // HTTP/2 fingerprinting signals
    // Set by Http2FingerprintContributor
    // ==========================================

    /// <summary>String: HTTP protocol version (e.g., HTTP/2, HTTP/1.1)</summary>
    public const string H2Protocol = "h2.protocol";

    /// <summary>String: Client type inferred from HTTP/2 fingerprint</summary>
    public const string H2ClientType = "h2.client_type";

    /// <summary>Bool: true when the request arrived via a proxy (so the observed h2 settings describe the proxy, not the originating client).</summary>
    public const string H2BehindProxy = "h2.behind_proxy";

    /// <summary>Bool: true when the request was negotiated as HTTP/2 (false for HTTP/1.x).</summary>
    public const string H2IsHttp2 = "h2.is_http2";

    /// <summary>Double in [0,1]: rolling fraction of recent requests in the same UA bucket that arrived over HTTP/2 (deployment norm).</summary>
    public const string H2PopulationHttp2Rate = "h2.population_http2_rate";

    /// <summary>Int: number of samples contributing to <see cref="H2PopulationHttp2Rate"/>.</summary>
    public const string H2PopulationSamples = "h2.population_samples";

    /// <summary>Bool: emitted by Http2FingerprintContributor when it observes the connection has been upgraded to HTTP/3 (Http3FingerprintContributor takes over the analysis).</summary>
    public const string H2ObservedHttp3 = "h2.is_http3";

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

    /// <summary>Bool: true when the request was negotiated as HTTP/3 (QUIC).</summary>
    public const string H3IsHttp3 = "h3.is_http3";

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

    /// <summary>String: Unified client signature (HMAC-SHA256). Written by SignatureContributor at Priority 1.</summary>
    public const string PrimarySignature = "signature.primary";

    /// <summary>
    ///     Marker: learning writes are suppressed for this request (bypass key with
    ///     DisableLearningWrites, or impersonation). Every learning-write path must skip
    ///     when present. Raised once by SignatureAtom (Priority 1) so downstream write
    ///     atoms can read it via <c>sink.Detect(SignalKeys.LearningSuppressed)</c>.
    /// </summary>
    public const string LearningSuppressed = "learning.suppressed";

    /// <summary>MultiFactorSignatures: full per-factor signature set (IP+UA, IP+Plugin, etc.). Written by SignatureContributor.</summary>
    public const string SignatureMultifactor = "signature.multifactor";

    /// <summary>String (JSON): HMAC hashes of discriminatory headers. Written by SignatureContributor.</summary>
    public const string HeaderHashes = "signature.header_hashes";

    // Identity (metastable fingerprint match)
    // See docs/architecture/fingerprint-match.md
    // ==========================================

    /// <summary>float[D]: composed identity feature vector. Written by IdentityVectorContributor.</summary>
    public const string IdentityVector = "identity.vector";

    /// <summary>
    ///     float[D]: the raw, unnormalized identity feature vector. Use this when comparing per-dim
    ///     signal magnitudes against archetype raw centroids (variance-aware scoring). The
    ///     L2-normalized variant lives in <see cref="IdentityVector"/> and is preferred by cosine
    ///     consumers. Written by IdentityVectorContributor alongside the normalized vector; absent
    ///     on the encoder cache-hit fast path (consumers fall back to <see cref="IdentityVector"/>).
    /// </summary>
    public const string IdentityVectorRaw = "identity.vector.raw";

    /// <summary>double in [0,1]: average dimension-presence ratio for the composed vector.</summary>
    public const string IdentityVectorQuality = "identity.vector_quality";

    /// <summary>IReadOnlyDictionary&lt;string,object?&gt;: the raw-values dict the encoder consumed. Written by IdentityVectorContributor so the BrowserMode classifier can walk it without recomposing.</summary>
    public const string IdentityRawValues = "identity.raw_values";

    /// <summary>
    ///     String: the browser mode this request was classified as. Same browser,
    ///     different modes — a real Chrome user emits navigation on a page load,
    ///     xhr on API fetches, sub-resource on stylesheets, etc. One of the ids
    ///     declared in <c>Definitions/BrowserModes/*.yaml</c> (navigation, xhr,
    ///     sub-resource, signalr-negotiate, websocket-upgrade, prefetch, bot-raw,
    ///     unknown). Written by BrowserModeClassifierContributor.
    ///     See docs/architecture/composite-browser-mode-fingerprints.md.
    /// </summary>
    public const string IdentityBrowserMode = "identity.browser_mode";

    /// <summary>
    ///     Int: this fingerprint's centroid maturity for the matched browser mode
    ///     <em>after</em> the current request's absorption. Equal to <c>1</c> on
    ///     the first request that introduces this mode to the fingerprint. The
    ///     mix-deviation anomaly axis (composite spec step 6) reads this to gate
    ///     score contribution on <c>BrowserModeOptions.MinModeMaturityForArchetypeMatch</c>.
    ///     Written by FingerprintMatchContributor on the absorb path.
    /// </summary>
    public const string IdentityBrowserModeMaturity = "identity.browser_mode_maturity";

    /// <summary>
    ///     Double in [-1, 1]: raw cosine similarity between the request's
    ///     mode-vector and the winning centroid in the
    ///     <c>browser_mode</c> catalogue. Surfaces the nearest-centroid
    ///     classifier's confidence so callers (dashboard, gating logic) can
    ///     reason about "matched mode X at 0.92" without re-running the
    ///     cosine themselves. Written by
    ///     <c>BrowserModeClassifierContributor</c> alongside
    ///     <see cref="IdentityBrowserMode"/>. Per design spec D2 (centroids,
    ///     not rules) and <c>feedback_centroids_not_rules</c>.
    /// </summary>
    public const string IdentityBrowserModeSimilarity = "identity.browser_mode_similarity";

    /// <summary>
    ///     Bool: true when this is the first request the matched browser mode
    ///     was ever observed on this fingerprint. The verdict composer treats
    ///     a true emergence here as a per-request anomaly axis (composite spec
    ///     step 6, axis 5). Written by FingerprintMatchContributor.
    /// </summary>
    public const string IdentityBrowserModeUnseen = "identity.browser_mode_unseen";

    /// <summary>String (UUID): the matched (or newly allocated) fingerprint shape. Written by FingerprintMatchContributor.</summary>
    public const string IdentityFingerprintId = "identity.fingerprint_id";

    /// <summary>
    ///     String (opaque 16-hex from <c>Guid.NewGuid().ToString("N")[..16]</c>):
    ///     the durable entity handle. Multiple primary signatures rotate behind
    ///     one entity id; behavioural-similarity merges retroactively unify ids.
    ///     This is the identifier the dashboard URL surface uses. Computed by
    ///     <c>IDetectionArchive.ResolveEntityAsync(primarySignature)</c> -- exact-key
    ///     lookup wins fast; first-encounter allocates a fresh entity.
    /// </summary>
    public const string EntityId = "entity.id";

    /// <summary>String (UUID): Pass 1's L1 candidate (may differ from final fingerprint_id).</summary>
    public const string IdentityFingerprintL1 = "identity.fingerprint_l1";

    /// <summary>double: weighted-cosine score of the winning match.</summary>
    public const string IdentityMatchScore = "identity.match_score";

    /// <summary>bool: set when this request allocated a new fingerprint.</summary>
    public const string IdentityIsNewFingerprint = "identity.is_new_fingerprint";

    /// <summary>bool: set when L1 and L2 disagreed on the fingerprint.</summary>
    public const string IdentityIsCorrection = "identity.is_correction";

    /// <summary>bool: set when match score landed in [LooseThreshold, MergeThreshold).</summary>
    public const string IdentityRotationCandidate = "identity.rotation_candidate";

    /// <summary>
    ///     String: dispatch outcome reason from the slow-path coordinator when Pass 2 was
    ///     not run (e.g. "Coalesced", "SheddedQueueFull", "SheddedBreakerOpen",
    ///     "SheddedSamePerFpCap"). Absent when Pass 2 ran normally. Surfaces in the
    ///     dashboard so an operator can see when the system is shedding under pressure.
    /// </summary>
    public const string IdentitySlowPathShed = "identity.slow_path_shed";

    /// <summary>
    ///     double in [0,1]: EWMA-smoothed fraction of recent matches for this fingerprint
    ///     that landed in the ambiguity zone (Pass 2 correction, rotation candidate,
    ///     L1 confirm fail, allocation). High values reveal a fingerprint that persistently
    ///     fails to settle into a stable identity — rare for legit traffic, characteristic
    ///     of an adversary probing the gate semantics. See task #42.
    /// </summary>
    public const string IdentityAmbiguityPersistence = "identity.ambiguity_persistence";

    /// <summary>
    ///     bool: ambiguity_persistence above the configured threshold. Emitted as a
    ///     positive bot signal in its own right; a flat 30% bot probability bias is
    ///     applied via a contributor when set.
    /// </summary>
    public const string IdentityAmbiguityProbing = "identity.ambiguity_probing";

    /// <summary>
    ///     bool: true on the first request that allocates a brand-new fingerprint row
    ///     (no `fingerprint_keys` match). Written by FingerprintMatchContributor on the
    ///     allocate path. Async absorption / drift subscribers wake on this to warm
    ///     their per-fp state without polling the durable tier.
    /// </summary>
    public const string IdentityFingerprintFirstSeen = "identity.fingerprint_first_seen";

    /// <summary>
    ///     int: the configured threshold the fingerprint's `observation_count` just
    ///     crossed on this request (one of IdentityOptions.Vector.NotifyOnCountCrossings).
    ///     Written by FingerprintMatchContributor after RecordObservationAsync returns.
    ///     Wakes FingerprintAbsorptionService when a hot fingerprint accumulates enough
    ///     new observations to be worth folding into the centroid.
    /// </summary>
    public const string IdentityFingerprintObservationCountCrossed = "identity.fingerprint_observation_count_crossed";

    /// <summary>
    ///     bool: true on every request where the matched fingerprint's centroid_maturity
    ///     exactly equals IdentityOptions.Vector.AbsorptionMaturityThreshold. Written by
    ///     FingerprintMatchContributor. Subscribers (drift verifier, Task 7) use this as
    ///     a wake signal; idempotence under repeated emissions is the subscriber's
    ///     responsibility -- this fires on every matching request, not only the first.
    /// </summary>
    public const string IdentityFingerprintMaturityCrossed = "identity.fingerprint_maturity_crossed";

    /// <summary>double in [0,1]: EWMA of post-detection bot probability over recent observations of this fingerprint.</summary>
    public const string IdentityCachedBotProbability = "identity.cached_bot_probability";

    // No IdentityCachedRiskBand signal: the risk band is derived at read from the raw
    // facts (probability + claim_status + bot type), never stored or signalled.

    /// <summary>String: archetype_id this fingerprint currently most resembles (cached on the row).</summary>
    public const string IdentityClientType = "identity.client_type";

    /// <summary>double: weighted-cosine score to the inferred archetype.</summary>
    public const string IdentityClientTypeConfidence = "identity.client_type_confidence";

    /// <summary>String: archetype_id the fingerprint was originally seeded from (lineage).</summary>
    public const string IdentityClientTypeOrigin = "identity.client_type_origin";

    /// <summary>string: human-readable display name of the matched archetype (e.g. "Chrome on Windows", "python-requests"). Written by FingerprintMatchContributor whenever a match resolves to an archetype.</summary>
    public const string IdentityArchetypeName = "identity.archetype_name";

    /// <summary>string: YAML <c>archetype_kind</c> of the matched archetype (e.g. "human-browser", "verified-bot", "tool", "headless"). Written by FingerprintMatchContributor alongside <see cref="IdentityArchetypeName"/>. The composer requires <c>human-browser</c> to use the archetype name as the visitor's display name -- bot-shaped kinds matching by signal coincidence don't get to override the UA family label.</summary>
    public const string IdentityArchetypeKind = "identity.archetype_kind";

    /// <summary>string?: optional descriptive text for the matched archetype. Written by FingerprintMatchContributor when present on the archetype.</summary>
    public const string IdentityArchetypeDescription = "identity.archetype_description";

    /// <summary>string?: name of the layout slot with the largest width-normalised Fisher-weighted L2 distance between the observed identity vector and the matched archetype's centroid (e.g. "network.country", "hdr.sec_ch_ua_brands_ordered"). Written by FingerprintMatchContributor after a match. Null when no archetype matched or drift is below DriftEpsilon.</summary>
    public const string IdentityDriftTopSlot = "identity.drift_top_slot";

    /// <summary>double: width-normalised Fisher-weighted squared distance for the top-drift slot. Lower = closer to centroid. Width normalisation prevents wide LSH slots from auto-winning purely on dimension count.</summary>
    public const string IdentityDriftTopScore = "identity.drift_top_score";

    /// <summary>string?: coarse category prefix of the top-drift slot ("network", "locale", "hdr", "tool", etc.). Lets the synthesizer map drift to a label class without parsing the full slot name.</summary>
    public const string IdentityDriftTopCategory = "identity.drift_top_category";

    /// <summary>
    ///     string: the fingerprint's persisted display name. Stable across requests; the
    ///     matcher writes this on every match by reading the persisted name on the matched
    ///     <c>Fingerprint</c> row (or by computing + persisting it on first allocation, and
    ///     on the significant-drift recompute path). The aggregator's <c>PrimaryBotName</c>
    ///     reads from here so the response header / dashboard surface always carry a name —
    ///     never empty, regardless of bot/human classification.
    /// </summary>
    public const string IdentityDisplayName = "identity.display_name";

    // bool: transport-layer dims are zero on what should be TLS-fronted traffic.
    // Periodicity Detection
    // ==========================================

    /// <summary>Double: Coefficient of variation of inter-request intervals (low = periodic bot).</summary>
    public const string PeriodicityCV = "periodicity.cv";

    /// <summary>Double: Mean inter-request interval in seconds.</summary>
    public const string PeriodicityMeanInterval = "periodicity.mean_interval";

    /// <summary>Int: Dominant period lag from autocorrelation analysis.</summary>
    public const string PeriodicityDominantPeriod = "periodicity.dominant_period";

    /// <summary>Double: Autocorrelation peak strength (0-1, high = strong periodic signal).</summary>
    public const string PeriodicityPeakStrength = "periodicity.peak_strength";

    /// <summary>Double: Shannon entropy of hour-of-day distribution (low = concentrated/scheduled).</summary>
    public const string PeriodicityHourEntropy = "periodicity.hour_entropy";

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

    // ============================================================
    // Registry-client (OCI Distribution Spec / Docker Registry v2) signals
    // Set by RegistryClientSensor. registry.client is only raised when the UA
    // family is CORROBORATED by registry protocol behaviour (never UA alone).
    // ============================================================

    /// <summary>Boolean ("true"): a genuine registry client, UA family corroborated by v2 protocol behaviour.</summary>
    public const string RegistryClientDetected = "registry.client";

    /// <summary>String: registry client name (e.g. "Docker", "containerd", "Helm").</summary>
    public const string RegistryClientName = "registry.client.name";

    /// <summary>String: parsed client version (e.g. "24.0.7"). Absent when the UA carries no version.</summary>
    public const string RegistryClientVersion = "registry.client.version";

    /// <summary>Marker: the RegistryClient sensor evaluated a registry-relevant request (registry UA or /v2/ path).</summary>
    public const string RegistryV2Ran = "registry.v2.ran";

    /// <summary>String: the OCI v2 step observed (ping / manifest / blob / upload / tags / v2).</summary>
    public const string RegistryV2Step = "registry.v2.step";

    /// <summary>Marker: the request carried a registry manifest / blob Accept media type.</summary>
    public const string RegistryAcceptManifest = "registry.accept.manifest";

    /// <summary>Marker: the request carried a Bearer Authorization header (registry auth dance).</summary>
    public const string RegistryAuthBearer = "registry.auth.bearer";

    /// <summary>Marker: UA claims a registry client but showed NO v2 protocol behaviour (spoof-suspect; not lowered).</summary>
    public const string RegistryUaOnly = "registry.ua_only";

    /// <summary>Marker: registry-client corroboration on a Harbor management-API path (/api/v2.0/*) was earned by
    /// inherited trust (this fingerprint proved real /v2/ OCI behaviour recently), not by this request alone.</summary>
    public const string RegistryClientInheritedTrust = "registry.client.inherited_trust";

    // ============================================================
    // Webhook receiver signals - set by WebhookSensor. webhook.detected is only raised
    // when the behavioural shape (POST + JSON + a known signature header) is
    // corroborated by an IP-based signal (dominant source IP, verified delivery
    // record, or a provider's published IP range) - never by the signature header
    // alone, which is spoofable.
    // ============================================================

    /// <summary>Boolean ("true"): a corroborated webhook delivery (shape + IP-based corroborator).</summary>
    public const string WebhookDetected = "webhook.detected";

    /// <summary>Marker: the request matched the webhook behavioural shape (POST + JSON + a known signature header), observed for learning regardless of corroboration.</summary>
    public const string WebhookShape = "webhook.shape";

    /// <summary>String: named webhook provider (e.g. "Stripe", "GitHub") when the signature header names one.</summary>
    public const string WebhookProvider = "webhook.provider";

    /// <summary>Marker: the source IP is the learned dominant sender for this endpoint.</summary>
    public const string WebhookIpDominant = "webhook.ip_dominant";

    /// <summary>Marker: the source IP has a verified (2xx-heavy) delivery track record for this endpoint.</summary>
    public const string WebhookVerifiedRecord = "webhook.verified_record";

    /// <summary>String: the webhook receiver endpoint path.</summary>
    public const string WebhookEndpoint = "webhook.endpoint";

    // ============================================================
    // UTM / Ad Traffic signals - set by PiiQueryStringContributor
    // ============================================================

    /// <summary>True if any UTM parameter or click ID is present in the query string.</summary>
    public const string UtmPresent = "utm.present";

    /// <summary>HMAC-SHA256 hash of utm_source value (truncated, URL-safe base64).</summary>
    public const string UtmSourceHash = "utm.source_hash";

    /// <summary>HMAC-SHA256 hash of utm_medium value.</summary>
    public const string UtmMediumHash = "utm.medium_hash";

    /// <summary>HMAC-SHA256 hash of utm_campaign value.</summary>
    public const string UtmCampaignHash = "utm.campaign_hash";

    /// <summary>True if gclid (Google Ads click ID) is present.</summary>
    public const string UtmHasGclid = "utm.has_gclid";

    /// <summary>True if fbclid (Meta Ads click ID) is present.</summary>
    public const string UtmHasFbclid = "utm.has_fbclid";

    /// <summary>True if msclkid (Microsoft Ads click ID) is present.</summary>
    public const string UtmHasMsclkid = "utm.has_msclkid";

    /// <summary>True if ttclid (TikTok Ads click ID) is present.</summary>
    public const string UtmHasTtclid = "utm.has_ttclid";

    /// <summary>HMAC-SHA256 hash of whichever click ID is present.</summary>
    public const string UtmClickIdHash = "utm.click_id_hash";

    /// <summary>Inferred ad platform: "google", "meta", "microsoft", "tiktok", "paid_other", "organic".</summary>
    public const string UtmSourcePlatform = "utm.source_platform";

    /// <summary>True if Referer header is present and non-empty.</summary>
    public const string UtmReferrerPresent = "utm.referrer_present";

    /// <summary>True when click ID present but Referer absent or domain doesn't match source platform.</summary>
    public const string UtmReferrerMismatch = "utm.referrer_mismatch";

    // ============================================================
    // Click Fraud signals - set by ClickFraudContributor
    // ============================================================

    /// <summary>Weighted confidence score 0.0-1.0 that this is click fraud traffic.</summary>
    public const string ClickFraudConfidence = "clickfraud.confidence";

    /// <summary>Primary pattern name: datacenter_paid, referrer_spoof, immediate_bounce, engagement_void, or headless_paid.</summary>
    public const string ClickFraudPattern = "clickfraud.pattern";

    /// <summary>True if the request arrived via a paid ad (UTM or click ID present).</summary>
    public const string ClickFraudIsPaidTraffic = "clickfraud.is_paid_traffic";

    /// <summary>True once ClickFraudContributor has run (gate for downstream triggers).</summary>
    public const string ClickFraudChecked = "clickfraud.checked";

    // ==========================================
    // Ad traffic signals (commercial AdTrafficContributor)
    // Written by Stylobot.Commercial.AdIntelligence.Detection.AdTrafficContributor (priority 47)
    // Requires: utm.present = true AND clickfraud.checked = true
    // ==========================================

    // Double 0.0-1.0: cross-session campaign abuse score (log-scaled distinct signatures).
    // Bool: signature arrived from more than N distinct campaigns in 24h (cookie stuffing).
    // Bool: same click ID hash seen from multiple distinct signatures (click ID reuse).
    // Bool: source platform changed between sessions for this signature.
    // String: IAB IVT class -- "GIVT" or "SIVT". Absent if traffic is legitimate.
    // True once AdTrafficContributor has run (gate for downstream triggers).
    // Double 0.0-1.0: likelihood this form submission is fraudulent (conversion endpoints only).
    // Bool: conversion fraud confirmed -- score exceeded configured threshold.
    // String: conversion endpoint pattern that matched (e.g. "/register").
    // ==========================================
    // Cluster detection signals
    // Set by ClusterContributor when signature belongs to a discovered cluster
    // ==========================================

    /// <summary>String: Cluster type ("product" or "network")</summary>
    public const string ClusterType = "cluster.type";

    /// <summary>String: Cluster identifier hash</summary>
    public const string ClusterId = "cluster.id";

    /// <summary>Int: Number of signatures in the cluster</summary>
    public const string ClusterMemberCount = "cluster.member_count";

    /// <summary>String: Auto-generated cluster behavior label (e.g., "Rapid-Scraper")</summary>
    public const string ClusterLabel = "cluster.label";

    /// <summary>Double: Average bot probability across cluster members</summary>
    public const string ClusterAvgBotProbability = "cluster.avg_bot_probability";

    /// <summary>Double: Average intra-cluster behavioral similarity</summary>
    public const string ClusterAvgSimilarity = "cluster.avg_similarity";

    /// <summary>Double: Temporal activity density of cluster members</summary>
    public const string ClusterTemporalDensity = "cluster.temporal_density";

    // ==========================================
    // Spectral analysis signals
    // Set by ClusterContributor from FFT-based spectral feature extraction
    // ==========================================

    /// <summary>Double: Shannon entropy of timing spectrum [0,1]. Low = bot-like, high = human-like</summary>
    public const string ClusterSpectralEntropy = "cluster.spectral_entropy";

    /// <summary>Double: Dominant frequency in timing spectrum (fraction of Nyquist)</summary>
    public const string ClusterDominantFrequency = "cluster.dominant_frequency";

    /// <summary>Double: Energy ratio at harmonic frequencies of dominant. High = timer with harmonics</summary>
    public const string ClusterHarmonicRatio = "cluster.harmonic_ratio";

    /// <summary>Double: Peak-to-average magnitude ratio [0,1]. High = sharp spectral line (bot)</summary>
    public const string ClusterPeakToAvg = "cluster.peak_to_avg";

    // Double: Temporal correlation with other cluster members [0,1]. High = shared C2 timing
    // ==========================================
    // Geographic and network classification signals
    // Written by GeoDetection.Contributor, read by core filters for geo/network blocking
    // ==========================================

    /// <summary>String: ISO 3166-1 alpha-2 country code (e.g., "US", "CN")</summary>
    public const string GeoCountryCode = "geo.country_code";

    /// <summary>Boolean: true if connection is via VPN</summary>
    public const string GeoIsVpn = "geo.is_vpn";

    /// <summary>Boolean: true if connection is via proxy</summary>
    public const string GeoIsProxy = "geo.is_proxy";

    /// <summary>Boolean: true if connection is via Tor exit node</summary>
    public const string GeoIsTor = "geo.is_tor";

    /// <summary>Boolean: true if IP belongs to a hosting/cloud provider</summary>
    public const string GeoIsHosting = "geo.is_hosting";

    // ==========================================
    // Country reputation signals
    // Set by GeoChangeContributor from CountryReputationTracker
    // ==========================================

    /// <summary>Double: Decayed bot rate for the visitor's country (0.0 to 1.0)</summary>
    public const string GeoCountryBotRate = "geo.country_bot_rate";

    /// <summary>Int: Country rank by bot rate (1-based, lower = more bots)</summary>
    public const string GeoCountryBotRank = "geo.country_bot_rank";

    // ==========================================
    // Geographic drift signals
    // Set by GeoChangeContributor for country change detection
    // ==========================================

    /// <summary>Boolean: Whether geo change was checked for this signature</summary>
    public const string GeoChangeChecked = "geo.change.checked";

    /// <summary>Int: Number of distinct countries seen for this signature</summary>
    public const string GeoChangeDistinctCountries = "geo.change.distinct_countries";

    /// <summary>Int: Total number of country changes for this signature</summary>
    public const string GeoChangeTotalChanges = "geo.change.total_changes";

    /// <summary>Boolean: Whether country drift was detected</summary>
    public const string GeoChangeDriftDetected = "geo.change.drift_detected";

    /// <summary>String: Previous country code before drift</summary>
    public const string GeoChangePreviousCountry = "geo.change.previous_country";

    /// <summary>Boolean: Whether rapid country switching was detected (proxy rotation)</summary>
    public const string GeoChangeRapidDrift = "geo.change.rapid_drift";

    /// <summary>String: Country reputation level (high, very_high)</summary>
    public const string GeoChangeReputationLevel = "geo.change.reputation_level";

    // ==========================================
    // Signature convergence signals
    // Set by ClusterContributor when signature belongs to a converged family
    // ==========================================

    /// <summary>String: Family identifier for converged signatures</summary>
    public const string ConvergenceFamilyId = "convergence.family_id";

    /// <summary>Int: Number of signatures in the converged family</summary>
    public const string ConvergenceFamilySize = "convergence.family_size";

    /// <summary>String: Reason the family was formed (TemporalProximity, BehavioralSimilarity, HighBotProbabilityCluster)</summary>
    public const string ConvergenceFormationReason = "convergence.formation_reason";

    /// <summary>Double: Confidence score of the merge decision</summary>
    public const string ConvergenceMergeConfidence = "convergence.merge_confidence";

    // Boolean: Whether family members are coherent (no split candidates)
    // Double: Average bot probability across all family members
    // Int: Total request count across all family members
    // ==========================================
    // Response behavior signals
    // Set by ResponseBehaviorContributor from historical response analysis
    // ==========================================

    /// <summary>Boolean: true if ResponseCoordinator is registered and available</summary>
    public const string ResponseCoordinatorAvailable = "response.coordinator_available";

    /// <summary>String: Client signature (IP:UA hash) used for response history lookup</summary>
    public const string ResponseClientSignature = "response.client_signature";

    /// <summary>Boolean: true if historical response data exists for this client</summary>
    public const string ResponseHasHistory = "response.has_history";

    /// <summary>Int: Total number of recorded responses for this client</summary>
    public const string ResponseTotalResponses = "response.total_responses";

    /// <summary>Double: Aggregated response behavior score from ResponseCoordinator (0.0-1.0)</summary>
    public const string ResponseHistoricalScore = "response.historical_score";

    /// <summary>
    ///     Boolean: <c>true</c> when the response status code on the current
    ///     request came from the upstream origin, <c>false</c> when STYLOBOT
    ///     itself set the status (load-shed 503, policy block 403, throttle
    ///     429, honeypot 404, API-key rejection). Default semantics:
    ///     <c>absent</c> means upstream (back-compat with FOSS hosts that
    ///     don't yet stamp). Status-derived detector arms must read this with
    ///     <c>state.GetSignal&lt;bool?&gt;(ResponseFromUpstream) ?? true</c>
    ///     and stand down when it's <c>false</c> -- otherwise stylobot's own
    ///     enforcement response feeds back as additional bot evidence on the
    ///     next request, locking visitors at 100% bot from a single shed/block.
    ///     Persisted on every detection event / centroid sample so post-hoc
    ///     analyses can segment enforcement-shaped responses out of the
    ///     natural prior (per <c>feedback_centroid_learning_feedback_loop</c>).
    ///     Honeypot path detection still scores via its dedicated
    ///     <see cref="ResponseHoneypotHits"/> signal -- this gate only
    ///     suppresses the response-CODE-derived contribution, not the
    ///     honeypot-PATH-derived one.
    /// </summary>
    public const string ResponseFromUpstream = "response.from_upstream";

    /// <summary>Int: Number of honeypot path hits (accessing trap paths that should never be accessed)</summary>
    public const string ResponseHoneypotHits = "response.honeypot_hits";

    /// <summary>Int: Number of 404 responses received</summary>
    public const string ResponseCount404 = "response.count_404";

    /// <summary>Int: Number of unique 404 paths probed (high = systematic scanning)</summary>
    public const string ResponseUnique404Paths = "response.unique_404_paths";

    /// <summary>Boolean: true if systematic vulnerability scanning pattern detected</summary>
    public const string ResponseScanPatternDetected = "response.scan_pattern_detected";

    /// <summary>Boolean: true if nearly all responses are 404 (exclusive 404 pattern)</summary>
    public const string ResponseExclusive404 = "response.exclusive_404";

    /// <summary>
    ///     Boolean: <c>true</c> when upstream looks healthy (5xx + 4xx EWMAs
    ///     within thresholds, after the min-sample-count floor), <c>false</c>
    ///     during cold-start / origin-down windows. Stamped at orchestrator
    ///     entry so persisted detection events and centroid samples carry
    ///     the flag and post-hoc analyses can segment outage shape out of
    ///     the natural prior.
    /// </summary>
    public const string UpstreamHealthy = "upstream.healthy";

    /// <summary>
    ///     Boolean: <c>true</c> while the gateway is still in cold-start
    ///     warmup (process uptime under <c>GatewayWarmup:WarmupDuration</c>
    ///     OR total observed requests under <c>MinGatewaySamples</c>);
    ///     <c>false</c> once both floors are crossed. Sibling of
    ///     <see cref="UpstreamHealthy"/>: upstream-down protects
    ///     status-derived signals when the protected site is cold; warmup
    ///     protects BEHAVIOURAL signals when stylobot itself just booted
    ///     and behavioural classifiers haven't accumulated enough samples
    ///     to score reliably. Stamped at orchestrator entry on the
    ///     gateway-wide dimension only -- detectors that know their
    ///     per-signature observation count call
    ///     <c>GatewayWarmupGate.IsWarmedUp(count)</c> for the finer-grained
    ///     decision and skip behavioural contributions when it returns
    ///     <c>false</c>. Persisted on every detection event so post-hoc
    ///     centroid analyses can segment cold-start shape out of the
    ///     natural prior (per
    ///     <c>feedback_centroid_learning_feedback_loop</c>).
    /// </summary>
    public const string GatewayWarmup = "gateway.warmup";

    /// <summary>Int: Number of authentication failures (401/403 responses)</summary>
    public const string ResponseAuthFailures = "response.auth_failures";

    /// <summary>String: Auth struggle severity level ("mild", "moderate", "severe")</summary>
    public const string ResponseAuthStruggle = "response.auth_struggle";

    /// <summary>Int: Number of error/stack trace response patterns triggered</summary>
    public const string ResponseErrorPatternCount = "response.error_pattern_count";

    /// <summary>Boolean: true if error template harvesting pattern detected</summary>
    public const string ResponseErrorHarvesting = "response.error_harvesting";

    /// <summary>Int: Number of rate limit (429) or block responses received</summary>
    public const string ResponseRateLimitViolations = "response.rate_limit_violations";

    // ==========================================
    // Verified bot identity signals
    // Set by VerifiedBotContributor after IP range / FCrDNS verification
    // ==========================================

    /// <summary>Boolean: true if verified bot check was performed</summary>
    public const string VerifiedBotChecked = "verifiedbot.checked";

    /// <summary>Boolean: true if bot identity was confirmed via IP range or FCrDNS</summary>
    public const string VerifiedBotConfirmed = "verifiedbot.confirmed";

    /// <summary>String: Verified or claimed bot name (e.g., "Googlebot")</summary>
    public const string VerifiedBotName = "verifiedbot.name";

    /// <summary>String: Verification method used.
    /// Canonical values: "ip_range" (CIDR match), "fcrdns" (forward-confirmed
    /// reverse DNS), "forward_dns" (FediverseDomainContributor: the claimed
    /// instance domain's A/AAAA records contain the client IP), "none".</summary>
    public const string VerifiedBotMethod = "verifiedbot.method";

    /// <summary>Boolean: true if UA claims bot identity but IP doesn't verify (spoofed)</summary>
    public const string VerifiedBotSpoofed = "verifiedbot.spoofed";

    /// <summary>Boolean: true if rDNS resolved but doesn't match domain claimed in UA</summary>
    public const string VerifiedBotRdnsMismatch = "verifiedbot.rdns_mismatch";

    /// <summary>Boolean: set by FediverseDomainContributor when it ran a forward-DNS
    /// lookup against the instance domain in the UA's <c>+https://host/</c> field
    /// and compared the resolved A/AAAA records to the client IP. <c>true</c> means
    /// the client IP appears in the resolved address set for that instance domain
    /// (the strongest IP-side corroboration we can produce for fediverse traffic,
    /// which has no fixed IP ranges). <c>false</c> means the lookup succeeded but
    /// the client IP was not in the result set -- the UA claim is spoofed even
    /// though NodeInfo confirmed the instance exists. Absent (no key) means the
    /// lookup was not attempted (no fediverse claim) or did not complete in time.
    /// Gap analysis 2026-06-15 (Gap #3): NodeInfo alone proved the instance hosts
    /// ActivityPub software but never bound the client IP to the claim.</summary>
    public const string VerifiedBotForwardDnsMatched = "verifiedbot.forward_dns_matched";

    /// <summary>String: set by FediverseDomainContributor when a forward-DNS lookup
    /// for the claimed instance domain failed (SocketException, OperationCanceledException,
    /// timeout). Value is the exception type name. Distinguishes "we tried and the
    /// lookup blew up" from "we never tried" (no signal) -- the verifier shouldn't
    /// be retried inline on the next request when DNS is just down, but the absence
    /// of the signal would otherwise be indistinguishable from never having a claim
    /// to verify.</summary>
    public const string VerifiedBotForwardDnsError = "verifiedbot.forward_dns_error";

    // ==========================================
    // ISP / residential IP signals
    // Set by IpContributor when ASN resolves to non-datacenter
    // ==========================================

    /// <summary>Boolean: true if IP belongs to an ISP/residential network (not a datacenter)</summary>
    public const string IpIsIsp = "ip.is_isp";

    // ==========================================
    // Attack pattern signals (HaxxorContributor)
    // Detects injection attempts, path probing, webshell scans, encoding evasion
    // ==========================================

    /// <summary>Boolean: true if any attack pattern was detected in request</summary>
    public const string AttackDetected = "attack.detected";

    /// <summary>String: comma-separated list of matched attack categories (e.g., "sqli,xss")</summary>
    public const string AttackCategories = "attack.categories";

    /// <summary>String: attack severity level (low, medium, high, critical)</summary>
    public const string AttackSeverity = "attack.severity";

    /// <summary>Boolean: SQL injection pattern detected</summary>
    public const string AttackSqli = "attack.sqli";

    /// <summary>Boolean: cross-site scripting pattern detected</summary>
    public const string AttackXss = "attack.xss";

    /// <summary>Boolean: path traversal pattern detected</summary>
    public const string AttackTraversal = "attack.traversal";

    /// <summary>Boolean: command injection pattern detected</summary>
    public const string AttackCmdi = "attack.cmdi";

    /// <summary>Boolean: server-side request forgery pattern detected</summary>
    public const string AttackSsrf = "attack.ssrf";

    /// <summary>Boolean: server-side template injection pattern detected</summary>
    public const string AttackSsti = "attack.ssti";

    /// <summary>Boolean: known vulnerable path probe detected (wp-admin, phpmyadmin, etc.)</summary>
    public const string AttackPathProbe = "attack.path_probe";

    /// <summary>Boolean: config file exposure scan detected (.env, appsettings.json, etc.)</summary>
    public const string AttackConfigExposure = "attack.config_exposure";

    /// <summary>Boolean: webshell probe detected (c99.php, r57.php, etc.)</summary>
    public const string AttackWebshellProbe = "attack.webshell_probe";

    /// <summary>Boolean: backup/dump file scan detected (.sql, .bak, etc.)</summary>
    public const string AttackBackupScan = "attack.backup_scan";

    /// <summary>Boolean: admin panel scan detected (/admin, /cpanel, /jenkins, etc.)</summary>
    public const string AttackAdminScan = "attack.admin_scan";

    /// <summary>Boolean: debug/actuator endpoint exposure detected</summary>
    public const string AttackDebugExposure = "attack.debug_exposure";

    /// <summary>Boolean: encoding evasion detected (double-encoding, null bytes, overlong UTF-8)</summary>
    public const string AttackEncodingEvasion = "attack.encoding_evasion";

    // ==========================================
    // Account takeover signals (AccountTakeoverContributor)
    // Detects credential stuffing, brute force, phishing ATO, behavioral drift
    // ==========================================

    /// <summary>Boolean: true if any ATO pattern was detected</summary>
    public const string AtoDetected = "ato.detected";

    /// <summary>Boolean: credential stuffing detected (high rate of failed logins)</summary>
    public const string AtoCredentialStuffing = "ato.credential_stuffing";

    /// <summary>Boolean: username enumeration detected (many unique usernames from same source)</summary>

    /// <summary>Boolean: password spray detected (same password hash across many signatures)</summary>

    /// <summary>Boolean: phishing-sourced ATO detected (new fingerprint + immediate sensitive action)</summary>

    /// <summary>Boolean: geographic velocity anomaly (impossible travel between logins)</summary>
    public const string AtoGeoVelocity = "ato.geo_velocity";

    /// <summary>Boolean: brute force detected (many login attempts from same source)</summary>
    public const string AtoBruteForce = "ato.brute_force";

    /// <summary>Boolean: direct POST to login without prior GET (skipped form page)</summary>
    public const string AtoDirectPost = "ato.direct_post";

    /// <summary>Boolean: rapid credential change after login (login -&gt; password change &lt; threshold)</summary>
    public const string AtoRapidCredentialChange = "ato.rapid_credential_change";

    /// <summary>Int: number of failed login attempts in current window</summary>
    public const string AtoLoginFailedCount = "ato.login_failed_count";

    /// <summary>Double: composite behavioral drift score (0.0-1.0), decay-adjusted</summary>
    public const string AtoDriftScore = "ato.drift_score";

    /// <summary>Boolean: geographic drift component of drift score</summary>
    public const string AtoDriftGeo = "ato.drift_geo";

    /// <summary>Boolean: fingerprint drift component (TLS/TCP mismatch)</summary>
    public const string AtoDriftFingerprint = "ato.drift_fingerprint";

    /// <summary>Double: timing drift component (request timing deviation)</summary>
    public const string AtoDriftTiming = "ato.drift_timing";

    /// <summary>Double: path drift component (navigation pattern change)</summary>
    public const string AtoDriftPath = "ato.drift_path";

    /// <summary>Double: velocity drift component (request rate deviation)</summary>
    public const string AtoDriftVelocity = "ato.drift_velocity";

    // ==========================================
    // Time-of-day signals
    // Set by TimeContributor on every request (foundation tier).
    // Webmaster use case: "stricter rules during off-hours" expressed as
    // `time.is_business_hours = false and bot.family = "scraper"`.
    // The timezone, business-hours window, and weekend definition all live
    // on <see cref="TimeOptions"/> so a webmaster can tune them per gateway.
    // ==========================================

    /// <summary>Int (0-23): hour of day in the gateway's configured <c>TimeOptions.TimeZone</c>.</summary>
    public const string TimeHourOfDay = "time.hour_of_day";

    /// <summary>String: short day-of-week token in the configured timezone — "mon" / "tue" / "wed" / "thu" / "fri" / "sat" / "sun".</summary>
    public const string TimeDayOfWeek = "time.day_of_week";

    /// <summary>Bool: true when <see cref="TimeDayOfWeek"/> is "sat" or "sun".</summary>
    public const string TimeIsWeekend = "time.is_weekend";

    /// <summary>Bool: true when <see cref="TimeHourOfDay"/> is within <c>[TimeOptions.BusinessHoursStart, TimeOptions.BusinessHoursEnd)</c> (exclusive end).</summary>
    public const string TimeIsBusinessHours = "time.is_business_hours";

    // ==========================================
    // Organisation signals (operator-toggleable)
    // Set by a commercial-side contributor that reads IOrgSignalStore (control plane
    // backed by Postgres). FOSS owns the key constant because policy predicates in
    // FOSS-side rule evaluators reference it; only the writer side is commercial.
    // Used by the lockdown-mode policy template -- a single org-wide kill switch
    // an operator can flip from the dashboard to drop traffic to humans-only / pinned
    // bots without redeploying or re-uploading rules.
    // ==========================================

    /// <summary>Bool: true when the operator has flipped the org-wide "lockdown" switch via the dashboard. Read by the <c>lockdown-mode</c> policy template's rule predicate. False when no row exists for the org (the default after a fresh install or before a paid plug-in is licensed).</summary>
    public const string OrgLockdown = "org.lockdown";

    // ==========================================
    // Transport protocol signals
    // Set by TransportProtocolContributor when analyzing upgrade/protocol headers
    // ==========================================

    /// <summary>String: detected transport protocol (http, websocket, grpc, grpc-web, graphql, sse)</summary>
    public const string TransportProtocol = "transport.protocol";

    /// <summary>Boolean: true if request is a protocol upgrade (WebSocket)</summary>
    public const string TransportIsUpgrade = "transport.is_upgrade";

    /// <summary>String: Sec-WebSocket-Version value from upgrade request</summary>
    public const string TransportWebSocketVersion = "transport.websocket_version";

    /// <summary>Boolean: true if Origin header is present on WebSocket upgrade</summary>
    public const string TransportWebSocketOrigin = "transport.websocket_origin";

    /// <summary>String: gRPC content-type value (application/grpc, application/grpc+proto)</summary>
    public const string TransportGrpcContentType = "transport.grpc_content_type";

    /// <summary>Boolean: true if GraphQL introspection query detected (__schema, __type)</summary>
    public const string TransportGraphqlIntrospection = "transport.graphql_introspection";

    /// <summary>Boolean: true if GraphQL batch query detected (array body)</summary>
    public const string TransportGraphqlBatch = "transport.graphql_batch";

    /// <summary>Boolean: true if SSE request detected (Accept: text/event-stream)</summary>
    public const string TransportSse = "transport.sse";

    // ==========================================
    // Two-level transport classification signals
    // Set by TransportProtocolContributor for downstream stream-aware detectors
    // ==========================================

    /// <summary>String: transport class - "http" | "websocket" | "sse"</summary>
    public const string TransportClass = "transport.transport_class";

    /// <summary>String: protocol class - "document" | "api" | "signalr" | "grpc" | "static" | "unknown"</summary>
    public const string TransportProtocolClass = "transport.protocol_class";

    /// <summary>Boolean: true if request is part of a SignalR connection (negotiate, connect, or long-poll)</summary>
    public const string TransportIsSignalR = "transport.is_signalr";

    /// <summary>String: SignalR transport type - "negotiate" | "websocket" | "sse" | "longpolling"</summary>
    public const string TransportSignalRType = "transport.signalr_type";

    /// <summary>Boolean: true if SSE reconnect detected (Last-Event-ID header present)</summary>
    public const string TransportSseReconnect = "transport.sse_reconnect";

    /// <summary>String: Last-Event-ID header value from SSE reconnect</summary>
    public const string TransportSseLastEventId = "transport.sse_last_event_id";

    /// <summary>Boolean: true if request uses any streaming transport (WebSocket, SSE, or SignalR)</summary>
    public const string TransportIsStreaming = "transport.is_streaming";

    // ===== Transport header trust (G1) =====

    /// <summary>bool: whether edge transport fingerprint headers were trusted for this request.</summary>
    public const string TransportHeadersTrusted = "transport.headers_trusted";

    /// <summary>string: reason for the trust verdict (AllowlistedPeer, PrivatePeer, NotAllowlisted, UntrustedPublicPeer, GateOff).</summary>
    public const string TransportTrustReason = "transport.trust_reason";

    /// <summary>bool: an untrusted direct peer sent edge transport fingerprint headers (possible spoof).</summary>
    public const string TransportSpoofedEdgeHeaders = "transport.spoofed_edge_headers";

    // ==========================================
    // Stream abuse detection signals
    // Set by StreamAbuseContributor for detecting attackers hiding behind streaming traffic
    // ==========================================

    /// <summary>Boolean: true if WebSocket handshake storm detected (excessive upgrades per signature)</summary>
    public const string StreamHandshakeStorm = "stream.handshake_storm";

    /// <summary>Boolean: true if cross-endpoint mixing detected (streaming + page-scraping from same signature)</summary>
    public const string StreamCrossEndpointMixing = "stream.cross_endpoint_mixing";

    /// <summary>Double: SSE reconnect rate (reconnects per minute)</summary>
    public const string StreamReconnectRate = "stream.reconnect_rate";

    /// <summary>Int: number of distinct streaming endpoint paths per signature</summary>
    public const string StreamConcurrentStreams = "stream.concurrent_streams";

    /// <summary>Boolean: true if stream abuse analysis was performed</summary>
    public const string StreamAbuseChecked = "stream.abuse_checked";

    // ==========================================
    // Action policy escalation signals (fail2ban-style)
    // Set by contributors to override policy evaluator and trigger action policies directly
    // ==========================================

    /// <summary>String: action policy name to trigger (e.g., "block-hard", "throttle-stealth")</summary>
    public const string ActionPolicyTrigger = "action.trigger_policy";

    /// <summary>String: human-readable reason for the triggered policy</summary>
    public const string ActionPolicyTriggerReason = "action.trigger_reason";

    /// <summary>Int: offense count that triggered the escalation</summary>
    public const string ActionPolicyEscalationCount = "action.escalation_count";

    // ==========================================
    // Session vector signals
    // Set by SessionVectorContributor for Markov-chain-based session analysis
    // ==========================================

    /// <summary>Int: number of requests in the current in-progress session</summary>
    public const string SessionRequestCount = "session.request_count";

    /// <summary>Int: number of completed session snapshots in history</summary>
    public const string SessionHistoryCount = "session.history_count";

    /// <summary>String: current request's Markov state (e.g., "PageView", "ApiCall")</summary>
    public const string SessionCurrentState = "session.current_state";

    /// <summary>Boolean: true if a session boundary was just detected (retrogressive)</summary>
    public const string SessionBoundaryDetected = "session.boundary_detected";

    /// <summary>Float: maturity score of the just-completed session (0-1)</summary>
    public const string SessionCompletedMaturity = "session.completed_maturity";

    /// <summary>Int: request count of the just-completed session</summary>
    public const string SessionCompletedRequestCount = "session.completed_request_count";

    /// <summary>String: dominant Markov state of the completed session</summary>
    public const string SessionDominantState = "session.dominant_state";

    /// <summary>
    ///     Cross-request inference: the fingerprint's PRIOR-persisted session established a
    ///     streaming interaction mode (Markov dominant/recent state in {WebSocket, SignalR,
    ///     ServerSentEvent}). Raised by SessionModeResolverAtom in the [6,20) priority gap from an
    ///     LFU read of the session by PrimarySignature. DISTINCT from <see cref="TransportIsStreaming"/>
    ///     (this-request transport truth) -- this is "the conversation is a streaming one", so
    ///     BehavioralAtom can treat repetition as NEUTRAL (mode-relative) even on a marker-less poll.
    ///     Consumers MUST still apply the mode-consistency gate (do not suppress when the current
    ///     pattern is content-scraping) so it cannot become a once-streamed-always-suppressed latch.
    /// </summary>
    public const string SessionEstablishedStreaming = "session.established_streaming";

    /// <summary>Float: maturity of the current session's vector (0-1)</summary>
    public const string SessionVectorMaturity = "session.vector_maturity";

    /// <summary>Float: cosine similarity of current session vs own history (0-1)</summary>
    public const string SessionSelfSimilarity = "session.self_similarity";

    /// <summary>Float: L2 magnitude of velocity vector between last two sessions</summary>
    public const string SessionVelocityMagnitude = "session.velocity_magnitude";

    /// <summary>Float[]: velocity vector between last two completed sessions</summary>
    public const string SessionVelocityVector = "session.velocity_vector";

    /// <summary>Float: gap-normalized velocity magnitude (magnitude / sqrt(gap_hours + 1)). High = fast rotation.</summary>
    public const string SessionVelocityGapNormalized = "session.velocity_gap_normalized";

    /// <summary>Float: L2 magnitude of the Markov-only component of the velocity vector (dims [0..N²])</summary>
    public const string SessionVelocityMarkovMagnitude = "session.velocity_markov_magnitude";

    /// <summary>Float: L2 magnitude of the temporal component of velocity (timing-only shift)</summary>
    public const string SessionVelocityTemporalMagnitude = "session.velocity_temporal_magnitude";

    /// <summary>Float: L2 magnitude of the fingerprint component of velocity (TLS/HTTP2/TCP shift = rotation trail)</summary>
    public const string SessionVelocityFingerprintMagnitude = "session.velocity_fingerprint_magnitude";

    /// <summary>Float: L2 magnitude of the acceleration vector (velocity between velocities). Zero = constant rotation rate.</summary>
    public const string SessionVelocityAcceleration = "session.velocity_acceleration";

    /// <summary>Boolean: fingerprint dims dominate the velocity vector (rotation trail pattern)</summary>
    public const string SessionVelocityIsFingerprintRotation = "session.velocity_is_fingerprint_rotation";

    /// <summary>String: name of the matched behavioral archetype from partial chain early detection</summary>
    public const string SessionPartialChainMatch = "session.partial_chain_match";

    /// <summary>Float: cosine similarity to the matched archetype</summary>
    public const string SessionPartialChainSimilarity = "session.partial_chain_similarity";

    /// <summary>Float: scaled confidence delta from partial chain archetype match</summary>
    public const string SessionPartialChainConfidence = "session.partial_chain_confidence";

    // Frequency fingerprinting
    /// <summary>Float[8]: autocorrelation at [1s,3s,10s,30s,1m,3m,10m,30m] lag scales</summary>
    public const string SessionFrequencyFingerprint = "session.frequency_fingerprint";

    /// <summary>Float: periodicity score [0,1] — how far from white noise (0=human, 1=bot rhythm)</summary>
    public const string SessionFrequencyPeriodicityScore = "session.frequency_periodicity_score";

    /// <summary>Int: dominant lag index (0-7) or -1 if aperiodic</summary>
    public const string SessionFrequencyDominantLag = "session.frequency_dominant_lag";

    // Trajectory modeling
    /// <summary>Float[129]: drift vector — linear regression slope over recent session vectors</summary>
    public const string SessionDriftVector = "session.drift_vector";

    /// <summary>Float: similarity of the predicted 24h-forward position to the nearest known bot pattern</summary>
    public const string SessionTrajectoryClusterSimilarity = "session.trajectory_cluster_similarity";

    /// <summary>Boolean: true if predicted trajectory lands inside a known attack cluster</summary>
    public const string SessionTrajectoryInAttackCluster = "session.trajectory_in_attack_cluster";

    // Void detection (novel behavior)
    /// <summary>Boolean: true if the current session is in empty shape-space (no similar sessions found)</summary>
    public const string SessionIsVoid = "session.is_void";

    /// <summary>Float: highest similarity score from the similarity search (0 if void)</summary>
    public const string SessionTopSimilarity = "session.top_similarity";

    /// <summary>Float: nearest Mahalanobis distance from variance-aware centroid search (lower = closer match)</summary>
    public const string SessionMahalanobisNearestDistance = "session.mahalanobis_nearest_distance";

    // ==========================================
    // Reactive pattern signals
    // Set by ReactivePatternContributor after analyzing post-4xx client behavior
    // ==========================================

    /// <summary>Int: number of error events recorded for this signature</summary>
    public const string ReactiveErrorEventCount = "reactive.error_event_count";

    /// <summary>Float: milliseconds since the last 4xx/5xx response was served to this signature</summary>
    public const string ReactivePost4xxGapMs = "reactive.post_4xx_gap_ms";

    /// <summary>Float: ratio of actual retry gap to Retry-After header value (1.0 = perfect compliance = bot-like)</summary>
    public const string ReactiveRetryAfterCompliance = "reactive.retry_after_compliance";

    /// <summary>Float [0,1]: 1.0 if current request is retrying a path that previously received a 403</summary>
    public const string ReactivePathPersistencePost403 = "reactive.path_persistence_post_403";

    /// <summary>Float [0,1]: fraction of error events on paths that received a 403 (high = path-targeted probe)</summary>
    public const string ReactivePathPersistenceRatio = "reactive.path_persistence_ratio";

    /// <summary>Float: coefficient of variation of consecutive retry gap ratios (low = mechanical geometric backoff)</summary>
    public const string ReactiveGeometricRatioCv = "reactive.geometric_ratio_cv";

    /// <summary>Float: mean ratio of consecutive retry gaps (2.0 = exponential, 1.618 = Fibonacci, 1.0 = linear)</summary>
    public const string ReactiveBackoffBase = "reactive.backoff_base";

    /// <summary>String: detected backoff pattern name (exponential, fibonacci, linear, mild_exponential, unknown, none)</summary>
    public const string ReactiveBackoffPattern = "reactive.backoff_pattern";

    /// <summary>Float [0,1]: monotone increase score of 429 gaps (high = automated rate adaptation)</summary>
    public const string ReactiveRateAdapted = "reactive.rate_adapted";

    /// <summary>Float: 1.0 if multiple signatures are retrying the same blocked paths simultaneously</summary>
    public const string ReactiveCoordinatedRetry = "reactive.coordinated_retry";

    /// <summary>Int: number of other signatures co-retrying the same blocked paths</summary>
    public const string ReactiveCoRetryerCount = "reactive.co_retryer_count";

    // ==========================================
    // Claimed Identity signals
    // Set by ClaimedIdentityContributor
    // ==========================================

    /// <summary>String: canonical UA family name resolved against profile centroids</summary>
    public const string ClaimedIdentityFamily = "claimed_identity.family";

    /// <summary>String: profile tier for the resolved family (browser, crawler, tool, reader, unknown)</summary>
    public const string ClaimedIdentityTier = "claimed_identity.tier";

    /// <summary>Double [0,1]: weighted similarity between observed signals and UA family centroid (low = mismatch)</summary>
    public const string ClaimedIdentityConsistencyScore = "claimed_identity.consistency_score";

    /// <summary>Bool: false when no seed profile exists for this UA family</summary>
    public const string ClaimedIdentityHasProfile = "claimed_identity.has_profile";

    // ==========================================
    // Intent / Threat scoring signals
    // Set by IntentContributor from session activity analysis
    // ==========================================

    /// <summary>Double: unified threat score (0.0 = benign, 1.0 = malicious)</summary>
    public const string IntentThreatScore = "intent.threat_score";

    /// <summary>String: threat band classification (None, Low, Elevated, High, Critical)</summary>
    public const string IntentThreatBand = "intent.threat_band";

    /// <summary>String: intent category (browsing, scraping, scanning, attacking, reconnaissance, monitoring, abuse)</summary>
    public const string IntentCategory = "intent.category";

    /// <summary>Boolean: true if LLM was used to classify this session's intent</summary>

    /// <summary>Double: highest similarity score from intent HNSW index</summary>
    public const string IntentSimilarityScore = "intent.similarity_score";

    /// <summary>Int: number of similar intent patterns found above threshold</summary>
    public const string IntentMatchCount = "intent.match_count";

    /// <summary>Boolean: true if intent classification is ambiguous (0.3-0.7 threat score)</summary>
    public const string IntentAmbiguous = "intent.ambiguous";

    /// <summary>Boolean: true if intent analysis was performed</summary>
    public const string IntentAnalyzed = "intent.analyzed";

    // ==========================================
    // Challenge verification signals
    // Set by ChallengeVerificationContributor when a PoW challenge was previously solved
    // ==========================================

    /// <summary>Boolean: true when a PoW challenge was verified for this signature</summary>
    public const string ChallengeVerified = "challenge.verified";

    /// <summary>Double: total solve duration in milliseconds</summary>
    public const string ChallengeSolveDurationMs = "challenge.solve_duration_ms";

    /// <summary>Double: timing jitter (CV of per-puzzle timings)</summary>
    public const string ChallengeTimingJitter = "challenge.timing_jitter";

    /// <summary>Integer: number of Web Workers reported by the client</summary>
    public const string ChallengeWorkerCount = "challenge.worker_count";

    /// <summary>Integer: number of puzzles in the challenge</summary>
    public const string ChallengePuzzleCount = "challenge.puzzle_count";

    // ==========================================
    // Fingerprint approval signals
    // Set by FingerprintApprovalContributor when a fingerprint has been manually approved
    // ==========================================

    /// <summary>Boolean: true when a fingerprint approval exists and was checked</summary>
    public const string ApprovalVerified = "approval.verified";

    /// <summary>String: approval status - "active", "expired", "revoked", "dimension_mismatch"</summary>
    public const string ApprovalStatus = "approval.status";

    /// <summary>Boolean: true when all locked dimensions match live signals</summary>
    public const string ApprovalLockedDimensionsOk = "approval.locked_dimensions_ok";

    /// <summary>String: comma-separated list of locked dimension keys that didn't match</summary>
    public const string ApprovalDimensionMismatch = "approval.dimension_mismatch";

    /// <summary>String: operator's justification for the approval</summary>
    public const string ApprovalJustification = "approval.justification";

    /// <summary>String: ISO 8601 expiry timestamp of the approval</summary>
    public const string ApprovalExpiresAt = "approval.expires_at";

    // ==========================================
    // Web Bot Auth (RFC 9421 HTTP Message Signatures) — Contract C3
    // Set by WebBotAuthApprovalAtom. Locked signal names; do not rename.
    // ==========================================

    /// <summary>
    ///     Bool presence-signal: request carried Signature + Signature-Input headers.
    ///     Beacon only — does not indicate valid verification. Raised before
    ///     the verify call so downstream atoms can gate on WBA presence without
    ///     waiting for the crypto outcome.
    /// </summary>
    public const string WebBotAuthPresented = "webbotauth.presented";

    /// <summary>Bool hint: true when the signature verified as Valid.</summary>
    public const string VerifiedBotSigned = "identity.verified_bot_signed";

    /// <summary>String hint: resolved agent name from the public key registry (WBA path).</summary>
    public const string WbaVerifiedBotName = "identity.verified_bot_name";

    /// <summary>String hint: public key identifier from the Signature-Input params.</summary>
    public const string VerifiedBotKeyId = "identity.verified_bot_key_id";

    /// <summary>String hint: algorithm string from the Signature-Input params.</summary>
    public const string VerifiedBotAlgorithm = "identity.verified_bot_algorithm";

    /// <summary>String hint: <see cref="Mostlylucid.BotDetection.Auth.TokenOutcome"/> as a string.</summary>
    public const string SignatureVerdict = "identity.signature_verdict";

    // ==========================================
    // CVE / Threat Intelligence signals
    // Set by CveFingerprintContributor when traffic matches CVE-derived fingerprints
    // ==========================================

    /// <summary>Int: number of CVE fingerprints that matched the session shape.</summary>
    public const string CveMatchCount = "cve.match_count";

    /// <summary>String: advisory ID of the top CVE match (e.g., "GHSA-xxxx" or "CVE-2026-1234").</summary>
    public const string CveTopAdvisoryId = "cve.top_advisory_id";

    /// <summary>Double: cosine similarity of the top CVE match (0-1).</summary>
    public const string CveTopSimilarity = "cve.top_similarity";

    /// <summary>String: severity of the top CVE match (critical/high/medium/low).</summary>
    public const string CveTopSeverity = "cve.top_severity";

    /// <summary>String: Leiden cluster label if the match belongs to an exploit family.</summary>
    public const string CveClusterLabel = "cve.cluster_label";

    /// <summary>String: comma-separated list of all matched CVE advisory IDs.</summary>
    public const string CveMatchedIds = "cve.matched_ids";

    // ==========================================
    // CVE Probe Detection (Simulation Packs)
    // Set by CveProbeContributor when request matches a simulation pack honeypot or CVE probe path
    // ==========================================

    /// <summary>Boolean: true if a CVE probe was detected from a simulation pack.</summary>
    public const string CveProbeDetected = "cve.probe.detected";

    /// <summary>String: CVE ID of the matched probe (e.g., "CVE-2024-6386").</summary>
    public const string CveProbeId = "cve.probe.id";

    /// <summary>String: severity of the matched CVE probe (critical/high/medium/low).</summary>
    public const string CveProbeSeverity = "cve.probe.severity";

    /// <summary>String: simulation pack ID that matched (e.g., "wordpress-5.9").</summary>
    public const string CveProbePackId = "cve.probe.pack_id";

    /// <summary>Boolean: true if request matched any simulation pack path (honeypot or CVE).</summary>
    public const string SimulationPackMatch = "simulation.pack.match";

    // ==========================================
    // Threat-intel enrichment signals
    // Set by ThreatIntelContributor; reads cached verdicts from
    // IThreatIntelCoordinator (offline providers: Spamhaus, Tor, KEV, cloud
    // ranges; live providers: GreyNoise / AbuseIPDB in commercial). Hot-path
    // safe - all lookups hit in-memory caches.
    // ==========================================

    /// <summary>Double in [0,1]: max Confidence across all providers that returned a verdict.</summary>
    public const string ThreatIntelScore = "threatintel.score";

    /// <summary>String: semicolon-joined unique classifications (e.g., "malicious;tor").</summary>
    public const string ThreatIntelClassifications = "threatintel.classifications";

    /// <summary>String: semicolon-joined names of providers that returned a verdict.</summary>
    public const string ThreatIntelProvidersHit = "threatintel.providers_hit";

    /// <summary>Boolean: true if any provider classified the IP as tor.</summary>
    public const string ThreatIntelTor = "threatintel.tor";

    /// <summary>String: CVE id matched by the CISA KEV provider (empty when no match).</summary>
    public const string ThreatIntelKevMatch = "threatintel.kev_match";

    /// <summary>
    ///     String: semicolon-joined unique intelligence classes that fired
    ///     (e.g., <c>"Vulnerability;SuspiciousNetworkRange"</c>). Lets policy
    ///     reason about <em>kind</em> of risk, not just a scalar score - a
    ///     Vulnerability class at <c>/.env</c> gates differently from a
    ///     CloudInfrastructure class at <c>/static/logo.png</c>.
    /// </summary>
    public const string IntelClasses = "intel.classes";

    /// <summary>String: <c>"Static"</c>, <c>"Normal"</c>, or <c>"Sensitive"</c>. Derived from the request path.</summary>
    public const string EndpointRisk = "endpoint.risk";

    /// <summary>Boolean: shortcut for <c>endpoint.risk == "Sensitive"</c>. Lets transition rules read a single bool.</summary>
    public const string EndpointRiskSensitive = "endpoint.risk_sensitive";

    /// <summary>
    ///     Boolean: at least one intelligence verdict AND the endpoint is sensitive.
    ///     A natural transition trigger: <c>"intel evidence + risky surface"</c> in one signal.
    /// </summary>
    public const string IntelHardGate = "intel.hard_gate";

    // ==========================================
    // Suspicious-change risk signals
    // Set by IdentityChangeContributor when this request's surface dims (geo
    // country, ASN, UA family, datacenter / Tor flags) diverge from the
    // matched fingerprint's last observation. Indicator-only stub for the
    // commercial API-protection feature (alerting / blocking on key-theft
    // cadence shifts) - FOSS contributors write the signals; FOSS policy
    // doesn't act on them at thresholds, commercial layers do.
    // ==========================================

    /// <summary>Boolean: this request's GeoCountryCode differs from the matched fingerprint's prior observation - large geo jumps are the canonical "credentials shared with another party" signal.</summary>
    public const string RiskCountryChanged = "risk.country_changed";

    /// <summary>String: "PREV_CC -> NEW_CC" formatted transition, written when RiskCountryChanged is true. Lets log lines and dashboards show the change without re-joining state.</summary>
    public const string RiskCountryTransition = "risk.country_transition";

    /// <summary>Boolean: this request's IpAsn differs from the matched fingerprint's prior observation - even within the same country, an ASN change can indicate rotation through proxies or VPN exits.</summary>
    public const string RiskAsnChanged = "risk.asn_changed";

    /// <summary>Boolean: this request's UserAgentFamily differs from the matched fingerprint's prior observation - very rare for genuine users, common for credential theft (the new caller's browser doesn't match the original visitor's).</summary>
    public const string RiskUaFamilyChanged = "risk.ua_family_changed";

    /// <summary>Boolean: this request appears from a datacenter / Tor IP but prior observations did not - infrastructure shift on the same identity, often the first observable when an API key gets exfiltrated to a botnet.</summary>
    public const string RiskInfrastructureIntroduced = "risk.infrastructure_introduced";

    /// <summary>
    ///     Boolean: this request's <see cref="ClientSideShapeHash"/> differs
    ///     from the matched fingerprint's prior observation. Stronger than
    ///     <see cref="RiskUaFamilyChanged"/> because the canvas + WebGL triple
    ///     is hardware-derived and effectively immutable for a real user --
    ///     a change under the same fingerprint id is the canonical
    ///     anti-detect-browser profile-swap signal (Multilogin Mimic and
    ///     Kameleo Chroma cycle profiles per session, and the profile carries
    ///     the canvas+WebGL identity).
    /// </summary>
    public const string RiskShapeHashChanged = "risk.shape_hash_changed";

    /// <summary>
    ///     Boolean: this request's <see cref="ClientSideBotdKind"/> differs
    ///     from the matched fingerprint's prior observation. Medium-strength
    ///     drift signal -- a fingerprint that BotD classified as
    ///     <c>selenium</c> last session and <c>puppeteer</c> this session
    ///     either swapped automation framework (rare for legitimate operators)
    ///     or is being reused across accounts.
    /// </summary>
    public const string RiskBotdKindChanged = "risk.botd_kind_changed";

    /// <summary>Double in [0,1]: weighted aggregate of the above flags. Stays well under 1.0 even for "everything changed" - this is an indicator only; FOSS doesn't gate policy on it directly, commercial layers thresholds and alerting on top.</summary>
    public const string RiskSuspiciousChangeScore = "risk.suspicious_change_score";

    /// <summary>String: human-readable summary of what changed (e.g. "country US -> RU; UA family Chrome -> python-requests"). Empty when nothing changed. Drives the dashboard / log message.</summary>
    public const string RiskSuspiciousChangeReason = "risk.suspicious_change_reason";

    /// <summary>
    ///     Boolean (presence): the matched fingerprint's durable, accumulated
    ///     drift-frequency EWMA (<see cref="Identity.Fingerprint.DriftFrequency"/>)
    ///     is at or above the atom's high threshold. A fingerprint whose surface
    ///     dims change often over time is the anti-detect / profile-cycling
    ///     browser signature (Multilogin / Kameleo rotating canvas + geo + UA per
    ///     session), so the CURRENT request scores as suspicious just for being
    ///     one of that fingerprint's requests - independent of whether THIS
    ///     request itself diverged (<see cref="RiskSuspiciousChangeScore"/>).
    /// </summary>
    public const string RiskDriftFrequencyHigh = "risk.drift_frequency_high";

    /// <summary>String: the matched fingerprint's accumulated drift-frequency EWMA figure, formatted to 3 decimals. Written alongside <see cref="RiskDriftFrequencyHigh"/> for dashboards / logs so the magnitude is visible without re-joining fingerprint state.</summary>
    public const string RiskDriftFrequency = "risk.drift_frequency";

    // ==========================================
    // Privacy / PII Detection signals
    // Set by PiiQueryStringContributor when PII patterns detected in query strings
    // ==========================================

    /// <summary>Boolean: true if PII was detected in the query string.</summary>
    public const string PrivacyQueryPiiDetected = "privacy.query_pii_detected";

    /// <summary>String: comma-separated list of detected PII types (e.g., "email,token").</summary>
    public const string PrivacyQueryPiiTypes = "privacy.query_pii_types";

    /// <summary>Boolean: true if PII was detected in an unencrypted (HTTP) request.</summary>
    public const string PrivacyUnencryptedPii = "privacy.unencrypted_pii";

    // ==========================================
    // JS Execution Timing signals
    // Set by BrowserFingerprintAnalyzer from client-side timing probes
    // Detects headless browsers with different timing characteristics
    // ==========================================

    /// <summary>Double: DOM layout timing in ms from requestAnimationFrame + getBoundingClientRect</summary>
    public const string JsLayoutTimeMs = "js.layout_time_ms";

    /// <summary>Double: setTimeout(1ms) actual drift in ms (actual - requested)</summary>
    public const string JsSetTimeoutDrift = "js.settimeout_drift";

    /// <summary>Double: minimum observable performance.now() resolution in ms</summary>
    public const string JsPerformanceResolution = "js.performance_resolution";

    /// <summary>Boolean: true if any JS timing anomaly was detected</summary>
    public const string JsTimingAnomaly = "js.timing_anomaly";

    /// <summary>Double: adaptive bias applied when a document request carries no fingerprint, scaled by population rate</summary>
    public const string ClientSideNoFingerprintBias = "clientside.no_fingerprint_bias";

    /// <summary>
    ///     Bool: the adblocker probe reported back that the browser blocked a real
    ///     ad-network resource. Set by <see cref="Orchestration.ContributingDetectors.ClientSideContributor"/>
    ///     from the stored <see cref="ClientSide.BrowserFingerprintResult.Adblocker"/>
    ///     flag. Treated as strong evidence that the visitor is human (overwhelmingly
    ///     bots don't run adblockers) AND suppresses the no-fingerprint penalty
    ///     because adblock extensions routinely block the fingerprint script too.
    ///     See docs/adblocker-detection.md.
    /// </summary>
    public const string ClientSideAdblockerDetected = "clientside.adblocker_detected";

    /// <summary>
    ///     String: ad-network provider alias the probe used (<c>"adsense"</c>,
    ///     <c>"amazon"</c>, <c>"medianet"</c>, <c>"custom"</c>).
    ///     <para>
    ///     Diagnostic-only -- written by <see cref="Orchestration.ContributingDetectors.ClientSideContributor"/>
    ///     but not consumed for any classification decision. Surfaces in the
    ///     BdfReplay probe list and dashboard signal inspector so operators can
    ///     see which provider URL the probe blocked. Don't gate any logic on
    ///     this value -- the boolean <see cref="ClientSideAdblockerDetected"/>
    ///     is the decision-relevant signal.
    ///     </para>
    /// </summary>
    public const string ClientSideAdblockerProvider = "clientside.adblocker_provider";

    /// <summary>
    ///     String: value of <c>navigator.connection.type</c> from the client-side
    ///     beacon (one of <c>wifi</c>, <c>cellular</c>, <c>ethernet</c>, <c>none</c>,
    ///     <c>bluetooth</c>, <c>wimax</c>, <c>mixed</c>, <c>other</c>, <c>unknown</c>;
    ///     empty when the API is unavailable). Set by
    ///     <see cref="Orchestration.ContributingDetectors.ClientSideContributor"/> from the stored
    ///     <see cref="ClientSide.BrowserFingerprintResult.ConnectionType"/>.
    ///     Consumed by <see cref="Orchestration.ContributingDetectors.InconsistencyContributor"/>
    ///     to flag mobile-claiming UAs paired with non-mobile connection classes
    ///     (the damru / Redroid-emulator pattern: real Android Chrome on a container
    ///     reports <c>ethernet</c> because <c>Network.overrideNetworkState</c> is
    ///     skipped).
    /// </summary>
    public const string ClientSideConnectionType = "clientside.connection_type";

    /// <summary>
    ///     Bool: the client-side WebRTC ICE probe completed (<c>createOffer()</c>
    ///     succeeded, gathering ran to timeout) without observing any <c>srflx</c>
    ///     candidate. On a real mobile network a STUN probe always produces at
    ///     least one srflx; absence indicates UDP egress is blocked at the OS or
    ///     proxy layer. Catches damru (iptables drops Chrome's UID), Bright Data
    ///     Scraping Browser (restricted egress in their hosted environment), and
    ///     locked-down corporate VMs. Consumed by
    ///     <see cref="Orchestration.ContributingDetectors.InconsistencyContributor"/> gated on
    ///     mobile UA-CH.
    /// </summary>
    public const string ClientSideIceNoSrflx = "clientside.ice_no_srflx";

    /// <summary>
    ///     Int: count of voices reported by <c>speechSynthesis.getVoices()</c> at
    ///     first paint. Real Android Chrome populates the list before the script
    ///     runs (the TTS engine starts at boot); damru runs a fresh Redroid
    ///     container per session and the voice list stays at 0 until first user
    ///     gesture. Consumed by <see cref="Orchestration.ContributingDetectors.InconsistencyContributor"/>
    ///     gated on a UA that contains "Android" (iOS Safari has its own voice
    ///     lifecycle so the check is Android-only to avoid false positives).
    /// </summary>
    public const string ClientSideTtsVoiceCount = "clientside.tts_voice_count";

    /// <summary>
    ///     String: FingerprintJS BotD verdict kind ("selenium", "puppeteer",
    ///     "phantomjs", "headless_chrome", "cefsharp", "awesomium", "nightmare",
    ///     etc.) when BotD classified the visitor as automated; null otherwise.
    ///     Written by <see cref="Orchestration.ContributingDetectors.ClientSideContributor"/>
    ///     from the stored fingerprint result for downstream consumers (cluster
    ///     attribution, dashboard "detected as X" surface, learning triggers).
    /// </summary>
    public const string ClientSideBotdKind = "clientside.botd_kind";

    /// <summary>
    ///     String: narrow "shape" hash of the fingerprint (canvas + WebGL
    ///     vendor + renderer). Stable per visitor, varies per Multilogin /
    ///     Kameleo curated profile. Consumed by
    ///     <c>PoolCollisionContributor</c> as the lookup key for
    ///     <see cref="Identity.IFingerprintPoolCollisionTracker"/>: same shape
    ///     observed under N+ distinct (IP, session) contexts within a sliding
    ///     window = fingerprint-pool collision.
    /// </summary>
    public const string ClientSideShapeHash = "clientside.shape_hash";

    /// <summary>
    ///     Int: count of distinct (IP-hash, session-id) contexts that have
    ///     produced the same fingerprint shape hash as the current request
    ///     within the configured window. Written by
    ///     <c>PoolCollisionContributor</c>. Above the threshold it emits a
    ///     bot contribution; the raw count surfaces for dashboard and learning
    ///     trigger consumption.
    /// </summary>
    public const string ClientSidePoolCollisionContexts = "clientside.pool_collision_contexts";

    /// <summary>
    ///     Bool: every sampled mousemove event had integer client x/y
    ///     coordinates (the Kameleo Chroma CDP-synthesised pattern). Real
    ///     mice produce sub-pixel float coords on any DPR &gt; 1. Consumed by
    ///     <see cref="Orchestration.ContributingDetectors.InconsistencyContributor"/> gated
    ///     on a desktop UA + non-trivial sample count.
    /// </summary>
    public const string ClientSideMouseAllIntegerCoords = "clientside.mouse_all_integer_coords";

    /// <summary>
    ///     Bool: observed JA3 string is a strict cipher-list subset of the
    ///     reference JA3 for the UA-claimed browser+version. Written by
    ///     <see cref="Orchestration.ContributingDetectors.TlsFingerprintContributor"/>'s
    ///     subset check. The damru cipher-blacklist signal -- catches the
    ///     entire ~184-variant family with one rule.
    /// </summary>
    public const string TlsCipherSubsetOfRealChrome = "tls.cipher_subset_of_real_chrome";

    /// <summary>
    ///     Int: number of ciphers missing from the observed JA3 versus the
    ///     reference JA3 for the UA-claimed browser+version. Only meaningful
    ///     when <see cref="TlsCipherSubsetOfRealChrome"/> is true. Larger
    ///     values = more aggressive blacklisting (damru ships up to 3
    ///     missing per profile rotation).
    /// </summary>
    public const string TlsCipherSubsetMissingCount = "tls.cipher_subset_missing_count";

    /// <summary>
    ///     Int: difference between the UA-claimed browser major version and
    ///     the JA3-matched browser major version (claim minus matched).
    ///     Positive when UA claims newer Chrome than the TLS fingerprint
    ///     supports -- the Multilogin Mimic / Kameleo Chroma pattern where
    ///     the patched Chromium fork's TLS lags Chrome stable by 1-2
    ///     releases. Written by
    ///     <see cref="Orchestration.ContributingDetectors.TlsFingerprintContributor"/>'s
    ///     version-delta check.
    /// </summary>
    public const string TlsVersionDeltaFromUa = "tls.version_delta_from_ua";

    /// <summary>
    ///     Double: coefficient of variation of inter-mouse-event timing
    ///     deltas (stddev / mean). Synthesised events show low CV; humans
    ///     run &gt; 0.5. Consumed alongside
    ///     <see cref="ClientSideMouseAllIntegerCoords"/> for Kameleo
    ///     detection.
    /// </summary>
    public const string ClientSideMouseTimingCv = "clientside.mouse_timing_cv";

    // ==========================================
    // Browser-characteristic consistency signals (client-attested, script v2.1.0+)
    // Raised by ClientSideAtom (the canonical clientside.* writer) from the beacon's
    // versionFeatures()/engineProbes() blocks. Inert observability at this stage;
    // the browser_char centroid scores them and the InconsistencyAtom emits the
    // derived verdict. These are the un-spoofable engine tells (weighted high in the
    // centroid mask) plus a feature-presence summary (spoofable, weighted low).
    // ==========================================

    /// <summary>
    ///     String: the real JS engine family inferred from Error().stack shape --
    ///     "v8" (Chromium), "spidermonkey-jsc" (Firefox/Safari), or "unknown".
    ///     Un-spoofable by mainstream tools; a claimed Safari/Firefox UA reporting
    ///     "v8" is a definitive spoof. Also the presence-trigger for the
    ///     browser-characteristic consistency branch.
    /// </summary>
    public const string ClientSideEngineFamily = "clientside.engine_family";

    /// <summary>
    ///     Bool: V8-only internals present (Intl.v8BreakIterator or
    ///     Error.captureStackTrace). True under a non-Chromium claimed UA is a spoof.
    /// </summary>
    public const string ClientSideEngineV8 = "clientside.engine_v8";

    /// <summary>
    ///     Int (0-5): count of version-gated capabilities present
    ///     (popover / :has() / findLast / structuredClone / WebGPU). Spoofable, so
    ///     low-weighted; used with the engine tells to score claim-vs-observed.
    /// </summary>
    public const string ClientSideFeatureCount = "clientside.feature_count";

    /// <summary>
    ///     Double (0-1): browser-characteristic drift -- how far this session's
    ///     feature/engine vector sits from the learned browser_char centroid for its
    ///     CLAIMED {family}:{major}:{mode}. Higher = more inconsistent with the claim.
    ///     Written by the InconsistencyAtom browser_char branch; only ever RAISES
    ///     suspicion (consistency is neutral, never a human discount).
    /// </summary>
    public const string BrowserCharacteristicDrift = "browser.characteristic_drift";


    // ==========================================
    // Cookie behavior signals
    // Set by CookieBehaviorContributor when analyzing cookie acceptance patterns
    // ==========================================

    /// <summary>Double: cookie acceptance rate (cookies returned / Set-Cookie sent). -1 if no Set-Cookie observed.</summary>
    public const string CookieAcceptanceRate = "cookie.acceptance_rate";

    /// <summary>Int: number of cookies in the current request's Cookie header.</summary>
    public const string CookieCount = "cookie.count";

    /// <summary>Boolean: true if cookies are being ignored (Set-Cookie sent but no cookies returned).</summary>
    public const string CookieIgnored = "cookie.ignored";

    // ==========================================
    // Resource Waterfall signals
    // Set by ResourceWaterfallContributor for document-to-asset ratio analysis
    // ==========================================

    /// <summary>Int: number of document/HTML requests from this signature.</summary>
    public const string ResourceDocumentCount = "resource.document_count";

    /// <summary>Int: number of sub-resource (CSS, JS, image, font) requests from this signature.</summary>
    public const string ResourceAssetCount = "resource.asset_count";

    /// <summary>Double: ratio of asset requests to document requests (healthy browsers >= 2.0).</summary>
    public const string ResourceAssetRatio = "resource.asset_ratio";

    /// <summary>Boolean: true if this signature has ever requested a font file.</summary>
    public const string ResourceFontRequested = "resource.font_requested";

    /// <summary>Boolean: true if this signature has requested /favicon.ico.</summary>
    public const string ResourceFaviconRequested = "resource.favicon_requested";

    // ==========================================
    // CDN / Proxy infrastructure signals
    // Set by infrastructure detection when proxy/CDN headers are present
    // ==========================================

    // String: detected CDN/proxy provider name (e.g., "cloudflare", "aws-alb")
    // String: header name used to extract the real client IP for this provider
    // ==========================================
    // Headless automation framework signals
    // Set by ClientSideContributor / UserAgentContributor when automation is identified
    // ==========================================

    /// <summary>String: specific automation framework name (e.g., "Puppeteer", "Playwright", "Selenium")</summary>
    public const string HeadlessFramework = "headless.framework";

    // ==========================================
    // Content Sequence signals
    // Written by ContentSequenceContributor (Priority 4).
    // Consumed by deferred detectors via TriggerConditions.
    // ==========================================

    /// <summary>Int: current position in the request sequence (0 = document hit).</summary>
    public const string SequencePosition = "sequence.position";

    /// <summary>Bool: true while actual requests match the expected Markov chain.</summary>
    public const string SequenceOnTrack = "sequence.on_track";

    /// <summary>Bool: true once the sequence has diverged from the expected chain.</summary>
    public const string SequenceDiverged = "sequence.diverged";

    /// <summary>Double: 0.0-1.0 divergence score for the current request.</summary>
    public const string SequenceDivergenceScore = "sequence.divergence_score";

    /// <summary>Int: sequence position at which the first divergence occurred.</summary>
    public const string SequenceDivergenceAtPosition = "sequence.divergence_at_position";

    /// <summary>String: UUID identifying the current content sequence context.</summary>
    public const string SequenceChainId = "sequence.chain_id";

    /// <summary>String: centroid classification — "Unknown", "Human", or "Bot".</summary>
    public const string SequenceCentroidType = "sequence.centroid_type";

    /// <summary>String: path of the document that started this sequence.</summary>
    public const string SequenceContentPath = "sequence.content_path";

    /// <summary>Bool: true when SignalR is the expected next Markov state and centroid is not Bot.</summary>
    public const string SequenceSignalRExpected = "sequence.signalr_expected";

    /// <summary>Bool: true when a prefetch request (Purpose: prefetch / Sec-Purpose: prefetch) is observed.</summary>
    public const string SequencePrefetchDetected = "sequence.prefetch_detected";

    /// <summary>Bool: true when no static assets appeared in the critical window — cache warm hit.</summary>
    public const string SequenceCacheWarm = "sequence.cache_warm";

    /// <summary>Bool: true when divergence rate for this endpoint is high enough to indicate content changed.</summary>
    public const string SequenceCentroidStale = "sequence.centroid_stale";

    /// <summary>Bool: true when a static asset's content fingerprint (ETag/Last-Modified) changed since last recorded.</summary>
    public const string AssetContentChanged = "asset.content_changed";

    // Fingerprint prior signals, written by SignatureVerdictGate on a Bias decision.
    // Consumed by FingerprintPriorContributor (Wave 0) to bias the orchestrator's
    // aggregation toward the cached verdict.

    /// <summary>Double in [0,1]: cached bot-probability the SignatureVerdictGate used to bias this request's aggregation.</summary>
    public const string FingerprintPriorProbability = "fingerprint.prior.probability";

    /// <summary>Double in [0,1]: confidence of the cached prior in <see cref="FingerprintPriorProbability"/>.</summary>
    public const string FingerprintPriorConfidence  = "fingerprint.prior.confidence";

    /// <summary>Int: age of the cached prior in seconds at the moment it was applied.</summary>
    public const string FingerprintPriorAgeSeconds  = "fingerprint.prior.age_seconds";

    /// <summary>Int: number of requests the cached prior has accumulated evidence over.</summary>
    public const string FingerprintPriorRequestCount = "fingerprint.prior.request_count";

    // ==========================================
    // PolicyScope vocabulary
    // Canonical per-request signals consulted by the composite PolicyScope
    // (Host? Method? Geo? Identity?) and predicates that reference the same
    // axes. These slots are matched by PolicyScopeMatcher; they double as
    // first-class predicate keys for the policy editor autocomplete.
    // ==========================================

    /// <summary>String: HTTP verb on the inbound request, uppercase (e.g. <c>"GET"</c>, <c>"POST"</c>).</summary>
    public const string RequestMethod = "request.method";

    /// <summary>String: apex domain of the request host (e.g. <c>"acme.com"</c>).</summary>
    public const string RequestDomain = "request.domain";

    /// <summary>String: full host / subdomain of the request (e.g. <c>"docs.acme.com"</c>).</summary>
    public const string RequestSubdomain = "request.subdomain";

    /// <summary>String: request path component (e.g. <c>"/api/upload"</c>).</summary>
    public const string RequestPath = "request.path";

    /// <summary>
    ///     Bool: <c>true</c> when the request path is a recognised health / readiness /
    ///     liveness probe endpoint (e.g. <c>/health</c>, <c>/healthz</c>, <c>/readyz</c>).
    ///     Raised by <c>HealthEndpointAtom</c> (Wave 0, Priority 2). Consumers can
    ///     skip bot-scoring or apply a neutral policy for these paths.
    /// </summary>
    public const string HealthEndpoint = "request.health_endpoint";

    /// <summary>
    ///     Bool: <c>true</c> when a health / readiness / liveness probe endpoint is hit by a
    ///     request that is NOT a legitimate expected probe (external source, or trusted source
    ///     with a browser-navigation shape). Raised by <c>HealthEndpointReconAtom</c>
    ///     (Priority 16, after IpAtom). Co-occurrence with other threat signals amplifies
    ///     the <c>intent.threat_score</c> nudge via <see cref="IntentThreatScore"/>.
    /// </summary>
    public const string HealthEndpointRecon = "health.endpoint_recon";

    /// <summary>
    ///     String: ISO-3166 alpha-2 country code, uppercase (e.g. <c>"US"</c>, <c>"RU"</c>).
    ///     Predicate-friendly alias of <see cref="GeoCountryCode"/> -- exposed under the
    ///     shorter <c>geo.country</c> name the PolicyScope authoring surface uses.
    /// </summary>
    public const string GeoCountry = "geo.country";

    /// <summary>String: named-bot family classification (e.g. <c>"googlebot"</c>, <c>"chatgpt"</c>).</summary>
    public const string IdentityNamedBot = "identity.named_bot";

    /// <summary>String: bot category classification (e.g. <c>"scraper"</c>, <c>"headless"</c>, <c>"crawler"</c>).</summary>
    public const string IdentityBotType = "identity.bot_type";

    /// <summary>String: human browser family (e.g. <c>"chrome"</c>, <c>"firefox"</c>, <c>"safari"</c>).</summary>
    public const string IdentityHumanBrowser = "identity.human_browser";

    /// <summary>
    ///     Bool: the orchestrator has dropped into load-shed mode for this
    ///     request. Foundation contributors ran; the classifier waves were
    ///     skipped because <see cref="Services.PipelineLoadSensor.CurrentBand"/>
    ///     reported <see cref="Services.LoadBand.Critical"/>. Surfaces so the
    ///     dashboard can flag shed-mode requests and audit can correlate to
    ///     the per-second RPS time series.
    /// </summary>
    public const string LoadShedActive = "load.shed_active";

    /// <summary>
    ///     String: enforcement-mode tag for the request, one of
    ///     <c>"natural" | "shed" | "throttle" | "block" | "challenge"</c>.
    ///     Closed-loop envelope (audit #8 +
    ///     <c>project_centroid_learning_feedback_loop</c>): persisted on
    ///     SessionRequest + detection events so centroid rollups can filter
    ///     to <c>"natural"</c> when computing the per-UA / per-archetype
    ///     prior. Stamped by the orchestrator at entry (default
    ///     <c>"natural"</c>) and overwritten by the action dispatcher when an
    ///     enforcement action fires.
    /// </summary>
    public const string EnforcementMode = "enforcement.mode";

    /// <summary>
    ///     String: active policy revision identifier (opaque). Closed-loop
    ///     envelope (audit #8): persisted alongside <see cref="EnforcementMode"/>
    ///     so behavioural shift can be correlated to policy edits. Producer is
    ///     the policy registry / dispatcher; null when no revision is
    ///     available (e.g. default policy).
    /// </summary>
    public const string PolicyRevision = "policy.revision";

    /// <summary>
    ///     Bool: <c>true</c> when this request was load-shed (mirror of
    ///     <see cref="LoadShedActive"/> exposed through the same signal
    ///     channel so SessionRequest's <c>Shed</c> field can be populated
    ///     from one consistent source).
    /// </summary>
    public const string Shed = "enforcement.shed";
}

/// <summary>Values written to <see cref="SignalKeys.TransportProtocolClass"/> by TransportProtocolContributor.</summary>
public static class TransportClasses
{
    public const string Document = "document";
    public const string Api = "api";
    public const string SignalR = "signalr";
    public const string Grpc = "grpc";
    public const string Static = "static";
    public const string Unknown = "unknown";
}