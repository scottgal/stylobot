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
    public const string UserAgentFamily = "ua.family";
    public const string UserAgentFamilyVersion = "ua.family_version";

    public const string HeadersMissing = "headers.missing";
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

    public const string ClientIp = "ip.address";
    public const string IpIsDatacenter = "ip.is_datacenter";
    public const string IpIsLocal = "ip.is_local";
    public const string IpProvider = "ip.provider";
    public const string IpAsn = "ip.asn";
    public const string IpAsnOrg = "ip.asn_org";
    public const string ProxyTopology = "proxy.topology";

    public const string FingerprintHeadlessScore = "fingerprint.headless_score";
    public const string FingerprintIntegrityScore = "fingerprint.integrity_score";

    // Stage 1 signals (behavioral)
    public const string BehavioralRateExceeded = "behavioral.rate_exceeded";
    public const string BehavioralAnomalyDetected = "behavioral.anomaly";

    // Stage 1 signals (version age)
    public const string VersionAgeAnalyzed = "versionage.analyzed";
    public const string BrowserVersionAge = "versionage.browser_age";

    // Stage 2 signals (meta-layers)
    public const string InconsistencyScore = "inconsistency.score";
    public const string InconsistencyDetails = "inconsistency.details";

    public const string RiskBand = "risk.band";
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
    public const string AiPrediction = "ai.prediction";
    public const string AiConfidence = "ai.confidence";
    public const string AiLearnedPattern = "ai.learned_pattern";

    // Heuristic signals (meta-layer that consumes all evidence)
    public const string HeuristicPrediction = "heuristic.prediction";
    public const string HeuristicConfidence = "heuristic.confidence";
    public const string HeuristicEarlyCompleted = "heuristic.early_completed";

    // Late heuristic signals (runs after all detectors, uses full evidence)
    public const string HeuristicLatePrediction = "heuristic.late_prediction";
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

    /// <summary>String: Unified client signature (HMAC-SHA256). Written by SignatureContributor at Priority 1.</summary>
    public const string PrimarySignature = "signature.primary";

    /// <summary>MultiFactorSignatures: full per-factor signature set (IP+UA, IP+Plugin, etc.). Written by SignatureContributor.</summary>
    public const string SignatureMultifactor = "signature.multifactor";

    /// <summary>String (JSON): HMAC hashes of discriminatory headers. Written by SignatureContributor.</summary>
    public const string HeaderHashes = "signature.header_hashes";

    // Identity (metastable fingerprint match)
    // See docs/architecture/fingerprint-match.md
    // ==========================================

    /// <summary>float[D]: composed identity feature vector. Written by IdentityVectorContributor.</summary>
    public const string IdentityVector = "identity.vector";

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
    ///     <c>ISessionStore.ResolveEntityAsync(primarySignature)</c> -- exact-key
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
    public const string FingerprintFirstSeen = "identity.fingerprint_first_seen";

    /// <summary>
    ///     int: the configured threshold the fingerprint's `observation_count` just
    ///     crossed on this request (one of IdentityOptions.Absorption.NotifyOnCountCrossings).
    ///     Written by FingerprintMatchContributor after RecordObservationAsync returns.
    ///     Wakes FingerprintAbsorptionService when a hot fingerprint accumulates enough
    ///     new observations to be worth folding into the centroid.
    /// </summary>
    public const string FingerprintObservationCountCrossed = "identity.fingerprint_observation_count_crossed";

    /// <summary>
    ///     bool: true on the first request where the matched fingerprint's centroid
    ///     maturity has just crossed IdentityOptions.Absorption.MaturityThreshold.
    ///     Written by FingerprintMatchContributor. Wakes drift verification because a
    ///     matured fingerprint's centroid is now load-bearing for display / verdict reads.
    /// </summary>
    public const string FingerprintMaturityThreshold = "identity.fingerprint_maturity_threshold";

    /// <summary>double in [0,1]: EWMA of post-detection bot probability over recent observations of this fingerprint.</summary>
    public const string IdentityCachedBotProbability = "identity.cached_bot_probability";

    /// <summary>String: cached risk band derived from cached_bot_probability.</summary>
    public const string IdentityCachedRiskBand = "identity.cached_risk_band";

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

    /// <summary>bool: transport-layer dims are zero on what should be TLS-fronted traffic.</summary>

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

    /// <summary>Double 0.0-1.0: cross-session campaign abuse score (log-scaled distinct signatures).</summary>

    /// <summary>Bool: signature arrived from more than N distinct campaigns in 24h (cookie stuffing).</summary>

    /// <summary>Bool: same click ID hash seen from multiple distinct signatures (click ID reuse).</summary>

    /// <summary>Bool: source platform changed between sessions for this signature.</summary>

    /// <summary>String: IAB IVT class -- "GIVT" or "SIVT". Absent if traffic is legitimate.</summary>

    /// <summary>True once AdTrafficContributor has run (gate for downstream triggers).</summary>

    /// <summary>Double 0.0-1.0: likelihood this form submission is fraudulent (conversion endpoints only).</summary>

    /// <summary>Bool: conversion fraud confirmed -- score exceeded configured threshold.</summary>

    /// <summary>String: conversion endpoint pattern that matched (e.g. "/register").</summary>

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

    /// <summary>Double: Temporal correlation with other cluster members [0,1]. High = shared C2 timing</summary>

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

    /// <summary>Boolean: Whether family members are coherent (no split candidates)</summary>

    /// <summary>Double: Average bot probability across all family members</summary>

    /// <summary>Int: Total request count across all family members</summary>

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

    /// <summary>String: Verification method used ("ip_range", "fcrdns", "none")</summary>
    public const string VerifiedBotMethod = "verifiedbot.method";

    /// <summary>Boolean: true if UA claims bot identity but IP doesn't verify (spoofed)</summary>
    public const string VerifiedBotSpoofed = "verifiedbot.spoofed";

    /// <summary>Boolean: true if rDNS resolved but doesn't match domain claimed in UA</summary>
    public const string VerifiedBotRdnsMismatch = "verifiedbot.rdns_mismatch";

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

    /// <summary>Boolean: rapid credential change after login (login -> password change < threshold)</summary>
    public const string AtoRapidCredentialChange = "ato.rapid_credential_change";

    /// <summary>Boolean: session behavioral anomaly detected post-login</summary>

    /// <summary>Int: number of failed login attempts in current window</summary>
    public const string AtoLoginFailedCount = "ato.login_failed_count";

    /// <summary>Int: number of unique username hashes seen in current window</summary>

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
    // License entitlement signals
    // Set by DomainEntitlementMiddleware (warn-never-lock; never affects request flow)
    // ==========================================

    /// <summary>Boolean: true when the request host did not match any licensed domain.</summary>

    /// <summary>String: the mismatch classification - "mismatch", "mismatch_cloud_pool", or "no_host".</summary>

    /// <summary>String: the normalized request host that triggered the mismatch.</summary>

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

    /// <summary>Double in [0,1]: weighted aggregate of the above flags. Stays well under 1.0 even for "everything changed" - this is an indicator only; FOSS doesn't gate policy on it directly, commercial layers thresholds and alerting on top.</summary>
    public const string RiskSuspiciousChangeScore = "risk.suspicious_change_score";

    /// <summary>String: human-readable summary of what changed (e.g. "country US -> RU; UA family Chrome -> python-requests"). Empty when nothing changed. Drives the dashboard / log message.</summary>
    public const string RiskSuspiciousChangeReason = "risk.suspicious_change_reason";

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
    ///     ad-network resource. Set by <see cref="ClientSide.ClientSideContributor"/>
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
    ///     Diagnostic-only -- written by <see cref="ContributingDetectors.ClientSideContributor"/>
    ///     but not consumed for any classification decision. Surfaces in the
    ///     BdfReplay probe list and dashboard signal inspector so operators can
    ///     see which provider URL the probe blocked. Don't gate any logic on
    ///     this value -- the boolean <see cref="ClientSideAdblockerDetected"/>
    ///     is the decision-relevant signal.
    ///     </para>
    /// </summary>
    public const string ClientSideAdblockerProvider = "clientside.adblocker_provider";


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

    /// <summary>String: detected CDN/proxy provider name (e.g., "cloudflare", "aws-alb")</summary>

    /// <summary>String: header name used to extract the real client IP for this provider</summary>

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
    public const string FingerprintPriorProbability = "fingerprint.prior.probability";
    public const string FingerprintPriorConfidence  = "fingerprint.prior.confidence";
    public const string FingerprintPriorAgeSeconds  = "fingerprint.prior.age_seconds";
    public const string FingerprintPriorRequestCount = "fingerprint.prior.request_count";
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