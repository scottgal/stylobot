using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Generates BDF v2 export documents from event store and visitor cache data.
///     Full zero-PII: no raw IP, no raw UA for humans, paths sanitized, IP-derived signals stripped.
///     Results are cached for 60s per signature to avoid repeated DB queries.
/// </summary>
public class BdfExportService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly VisitorListCache _visitorCache;
    private readonly ILogger<BdfExportService> _logger;

    /// <summary>Cache: signature → (document, expiry). Thread-safe, short TTL.</summary>
    private readonly ConcurrentDictionary<string, (BdfExportDocument Doc, DateTime Expiry)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCacheEntries = 500;

    private static readonly string? DetectorVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    // ── PII blocklists ──

    /// <summary>Signal keys that are ALWAYS stripped (raw PII).</summary>
    private static readonly HashSet<string> PiiSignalKeys =
    [
        SignalKeys.UserAgent,   // ua.raw — raw User-Agent string
        SignalKeys.ClientIp,    // ip.address — raw IP
    ];

    /// <summary>IP-derived signals stripped for all signatures (could fingerprint networks).</summary>
    private static readonly HashSet<string> IpDerivedSignalKeys =
    [
        SignalKeys.IpProvider,  // ip.provider — ISP/hosting name
        SignalKeys.IpAsn,       // ip.asn — ASN number
        SignalKeys.IpAsnOrg,    // ip.asn_org — ASN organization name
    ];

    /// <summary>UA classification keys — only included for bot-detected signatures.</summary>
    private static readonly HashSet<string> UaClassificationKeys =
    [
        SignalKeys.UserAgentIsBot,
        SignalKeys.UserAgentBotType,
        SignalKeys.UserAgentBotName,
        SignalKeys.UserAgentOs,
        SignalKeys.UserAgentBrowser
    ];

    /// <summary>Regex for path segments that look like IDs (GUIDs, long numbers, base64).</summary>
    private static readonly Regex IdSegmentPattern = new(
        @"^[0-9a-f\-]{8,}$|^\d{4,}$|^[A-Za-z0-9+/]{20,}={0,2}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BdfExportService(
        IDashboardEventStore eventStore,
        VisitorListCache visitorCache,
        ILogger<BdfExportService> logger)
    {
        _eventStore = eventStore;
        _visitorCache = visitorCache;
        _logger = logger;
    }

    /// <summary>
    ///     Generate a BDF v2 document for the given signature.
    ///     Returns cached result if available and fresh.
    /// </summary>
    public async Task<BdfExportDocument?> ExportAsync(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        // Check cache
        if (_cache.TryGetValue(signature, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.Doc;

        var doc = await BuildExportDocumentAsync(signature);
        if (doc == null)
            return null;

        // Evict stale entries if cache is too large
        if (_cache.Count > MaxCacheEntries)
            EvictStaleEntries();

        _cache[signature] = (doc, DateTime.UtcNow + CacheTtl);
        return doc;
    }

    private async Task<BdfExportDocument?> BuildExportDocumentAsync(string signature)
    {
        // 1. Get detections for this signature
        var filter = new DashboardFilter { SignatureId = signature, Limit = 100 };
        var detections = await _eventStore.GetDetectionsAsync(filter);

        if (detections.Count == 0)
        {
            _logger.LogDebug("BDF export: no detections found for signature {Signature}", signature);
            return null;
        }

        // Sort chronologically
        detections.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // 2. Get visitor cache for behavioral history
        var visitor = _visitorCache.Get(signature);

        // Use latest detection for summary data
        var latest = detections[^1];
        var isBotDetected = latest.IsBot;

        // 3. Build narrative/scenario string
        var scenario = latest.Narrative
            ?? $"{(latest.IsBot ? "Bot" : "Human")} — {latest.RiskBand} risk";

        // 4. Build request sequence with inter-request delays
        var requests = new List<BdfRequest>();
        var firstTimestamp = detections[0].Timestamp;

        for (var i = 0; i < detections.Count; i++)
        {
            var d = detections[i];
            var relativeTimestamp = (d.Timestamp - firstTimestamp).TotalSeconds;
            var delayAfter = i < detections.Count - 1
                ? (detections[i + 1].Timestamp - d.Timestamp).TotalSeconds
                : 0;

            // Reconstruct privacy-safe headers from signals
            var headers = ReconstructHeaders(d, isBotDetected);

            requests.Add(new BdfRequest
            {
                Timestamp = Math.Round(relativeTimestamp, 3),
                Method = d.Method,
                Path = SanitizePath(d.Path),
                Headers = headers,
                ExpectedStatus = d.StatusCode > 0 ? d.StatusCode : 200,
                DelayAfter = Math.Round(Math.Min(delayAfter, 30), 3),
                ExpectedDetection = new BdfExpectedDetection
                {
                    IsBot = d.IsBot,
                    BotProbability = Math.Round(d.BotProbability, 4),
                    RiskBand = d.RiskBand
                }
            });
        }

        // 5. Build detector contributions from latest detection
        Dictionary<string, BdfDetectorContribution>? detectorContributions = null;
        if (latest.DetectorContributions is { Count: > 0 })
        {
            detectorContributions = latest.DetectorContributions.ToDictionary(
                kv => kv.Key,
                kv => new BdfDetectorContribution
                {
                    ConfidenceDelta = Math.Round(kv.Value.ConfidenceDelta, 4),
                    Contribution = Math.Round(kv.Value.Contribution, 4),
                    Reason = kv.Value.Reason,
                    ExecutionTimeMs = Math.Round(kv.Value.ExecutionTimeMs, 3)
                });
        }

        // 6. Filter important signals (FULL zero-PII pass)
        var importantSignals = FilterPiiSignals(latest.ImportantSignals, isBotDetected);

        // 7. Build behavioral history from visitor cache
        BdfBehavioralHistory? behavioralHistory = null;
        if (visitor != null)
        {
            lock (visitor.SyncRoot)
            {
                behavioralHistory = new BdfBehavioralHistory
                {
                    BotProbabilityHistory = visitor.BotProbabilityHistory.ToList(),
                    ConfidenceHistory = visitor.ConfidenceHistory.ToList(),
                    ProcessingTimeHistory = visitor.ProcessingTimeHistory.ToList()
                };
            }
        }

        // 8. Determine expectation from the overall classification
        var expectation = new BdfExpectation
        {
            ExpectedClassification = isBotDetected ? "Bot" : "Human",
            MaxBotProbability = isBotDetected ? 1.0 : 0.3,
            MaxRiskBand = latest.RiskBand ?? "Medium"
        };

        // 9. Country code: keep for bots (detection signal), strip for humans (could be PII)
        var countryCode = isBotDetected ? latest.CountryCode : null;

        return new BdfExportDocument
        {
            ScenarioName = $"sig-{signature[..Math.Min(8, signature.Length)]}",
            Scenario = scenario,
            Confidence = Math.Round(latest.Confidence, 4),
            IsBot = isBotDetected,
            Signature = new BdfSignatureInfo
            {
                PrimarySignature = signature,
                Hits = visitor?.Hits ?? detections.Count,
                FirstSeen = detections[0].Timestamp,
                LastSeen = latest.Timestamp,
                RiskBand = latest.RiskBand,
                BotType = latest.BotType,
                BotName = latest.BotName,
                Action = latest.Action,
                CountryCode = countryCode,
                Narrative = latest.Narrative
            },
            BehavioralHistory = behavioralHistory,
            DetectorContributions = detectorContributions,
            ImportantSignals = importantSignals,
            Requests = requests,
            Expectation = expectation,
            Metadata = new BdfMetadata
            {
                ExportedUtc = DateTime.UtcNow,
                PiiLevel = "none",
                DetectorVersion = DetectorVersion
            }
        };
    }

    /// <summary>
    ///     Reconstruct privacy-safe request headers from detection signals.
    ///     Raw UA only included for bot signatures (matching TrainingDataEndpoints PII logic).
    /// </summary>
    private static Dictionary<string, string> ReconstructHeaders(
        DashboardDetectionEvent detection,
        bool isBotDetected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // UA: ONLY for bot-classified signatures — never for humans
        if (isBotDetected && !string.IsNullOrEmpty(detection.UserAgent))
            headers["User-Agent"] = detection.UserAgent;

        // Extract Sec-Fetch-* and other safe headers from signals
        if (detection.ImportantSignals != null)
        {
            foreach (var (key, value) in detection.ImportantSignals)
            {
                if (value == null) continue;
                var headerName = SignalKeyToHeaderName(key);
                if (headerName != null)
                    headers[headerName] = value.ToString() ?? "";
            }
        }

        return headers;
    }

    /// <summary>
    ///     Maps signal keys to HTTP header names for safe headers only.
    ///     Only privacy-safe protocol-level headers are mapped.
    /// </summary>
    private static string? SignalKeyToHeaderName(string signalKey)
    {
        return signalKey switch
        {
            "header.sec_fetch_site" => "Sec-Fetch-Site",
            "header.sec_fetch_mode" => "Sec-Fetch-Mode",
            "header.sec_fetch_dest" => "Sec-Fetch-Dest",
            _ => null
        };
    }

    /// <summary>
    ///     Sanitize path: strip query strings (may contain tokens/user IDs),
    ///     generalize ID-like segments.
    /// </summary>
    private static string SanitizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        // Strip query string (may contain tokens, session IDs, user data)
        var cleanPath = path.Split('?')[0];

        // Generalize ID-like segments
        var segments = cleanPath.Split('/');
        var generalized = string.Join("/", segments.Select(s =>
            IdSegmentPattern.IsMatch(s) ? "*" : s));

        return generalized;
    }

    /// <summary>
    ///     Full zero-PII signal filter. Strips:
    ///     - Raw PII (ua.raw, ip.address) — always
    ///     - IP-derived (ip.provider, ip.asn, ip.asn_org) — always
    ///     - UA classification (ua.is_bot, ua.bot_type, etc.) — only for humans
    /// </summary>
    private static Dictionary<string, object>? FilterPiiSignals(
        Dictionary<string, object>? signals, bool isBotDetected)
    {
        if (signals == null || signals.Count == 0)
            return null;

        var blocked = new HashSet<string>(PiiSignalKeys);

        // Always strip IP-derived signals (network fingerprinting risk)
        foreach (var key in IpDerivedSignalKeys)
            blocked.Add(key);

        // UA classification: only for bots
        if (!isBotDetected)
            foreach (var key in UaClassificationKeys)
                blocked.Add(key);

        var filtered = signals
            .Where(kv => !blocked.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return filtered.Count > 0 ? filtered : null;
    }

    /// <summary>Evict expired entries from cache.</summary>
    private void EvictStaleEntries()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cache)
        {
            if (kv.Value.Expiry < now)
                _cache.TryRemove(kv.Key, out _);
        }
    }
}
