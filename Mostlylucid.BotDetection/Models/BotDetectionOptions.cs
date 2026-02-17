using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Yarp;

namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Configuration options for bot detection.
///     Supports a range of detection strategies from simple (static patterns) to advanced (LLM).
///     All options are designed to be fail-safe - failures are logged but never crash the app.
/// </summary>
public class BotDetectionOptions
{
    // ==========================================
    // Core Detection Settings
    // ==========================================

    /// <summary>
    ///     Confidence threshold above which a request is classified as a bot (default: 0.7)
    ///     Valid range: 0.0 to 1.0
    ///     Lower values = more aggressive detection (more false positives)
    ///     Higher values = more conservative (fewer false positives, may miss some bots)
    /// </summary>
    public double BotThreshold { get; set; } = 0.7;

    /// <summary>
    ///     Enable test mode (allows ml-bot-test-mode header to override detection)
    ///     WARNING: Only enable in development/testing environments!
    ///     In production, this header is completely ignored for security.
    /// </summary>
    public bool EnableTestMode { get; set; }

    /// <summary>
    ///     Test mode simulations - mapping from test mode name to simulated User-Agent.
    ///     Used when EnableTestMode is true and ml-bot-test-mode header is sent.
    ///     The detection runs the REAL pipeline with the simulated User-Agent.
    ///     Example: { "googlebot": "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)" }
    /// </summary>
    public Dictionary<string, string> TestModeSimulations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Base64-encoded HMAC key for PII signature hashing (zero-PII architecture).
    ///     Must be at least 128 bits (16 bytes) when decoded.
    ///     If not provided, a random key will be auto-generated (suitable for dev only).
    ///     For production, store in Key Vault/environment variable and configure here.
    ///     Generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    /// </summary>
    public string? SignatureHashKey { get; set; }

    // ==========================================
    // Detection Strategy Toggles
    // ==========================================

    /// <summary>
    ///     Enable user-agent based detection (static pattern matching - fastest)
    ///     Matches against known bot signatures from Matomo, crawler-user-agents, etc.
    ///     Recommended: Always enable unless you have specific requirements.
    /// </summary>
    public bool EnableUserAgentDetection { get; set; } = true;

    /// <summary>
    ///     Enable header analysis (examines Accept, Accept-Language, etc.)
    ///     Detects missing or suspicious HTTP headers that bots often omit.
    ///     Low overhead, recommended for most use cases.
    /// </summary>
    public bool EnableHeaderAnalysis { get; set; } = true;

    /// <summary>
    ///     Enable IP-based detection (checks against datacenter IP ranges)
    ///     Identifies requests from AWS, Azure, GCP, and other cloud providers.
    ///     Useful for detecting automated traffic from servers.
    /// </summary>
    public bool EnableIpDetection { get; set; } = true;

    /// <summary>
    ///     Enable behavioral analysis (rate limiting, request patterns)
    ///     Monitors request frequency per IP address.
    ///     Requires memory to track request counts.
    /// </summary>
    public bool EnableBehavioralAnalysis { get; set; } = true;

    /// <summary>
    ///     Enable AI-based detection (Ollama or ONNX).
    ///     Uses a local model to analyze suspicious patterns.
    ///     Higher latency but can detect sophisticated bots.
    ///     Configure provider via AiDetection section.
    /// </summary>
    public bool EnableLlmDetection { get; set; }

    // ==========================================
    // AI Detection Settings (Ollama or ONNX)
    // ==========================================

    /// <summary>
    ///     Configuration for AI-based bot detection.
    ///     Supports Ollama (LLM) or ONNX (classification model).
    /// </summary>
    public AiDetectionOptions AiDetection { get; set; } = new();

    /// <summary>
    ///     Configuration for the background LLM classification coordinator.
    /// </summary>
    public LlmCoordinatorOptions LlmCoordinator { get; set; } = new();

    /// <summary>
    ///     When true, detections from local/private IPs are excluded from SignalR broadcasts
    ///     and the live feed. Prevents self-detection from contaminating production data.
    ///     Default: true (set to false for local development/testing).
    /// </summary>
    public bool ExcludeLocalIpFromBroadcast { get; set; } = true;

    /// <summary>
    ///     Request count threshold for triggering signature description synthesis.
    ///     When a signature (unique bot fingerprint) receives this many requests,
    ///     the LLM generates a human-readable name/description.
    ///     Set to 0 to disable. Default: 50 requests.
    /// </summary>
    public int SignatureDescriptionThreshold { get; set; } = 50;

    // ==========================================
    // Blocking Policy Settings
    // ==========================================

    /// <summary>
    ///     Enable automatic blocking of detected bots.
    ///     When false, bots are detected and logged but not blocked.
    ///     Use endpoint-specific [BlockBots] attributes for fine-grained control.
    /// </summary>
    public bool BlockDetectedBots { get; set; } = false;

    /// <summary>
    ///     HTTP status code to return when blocking bots.
    ///     Common values: 403 (Forbidden), 429 (Too Many Requests), 503 (Service Unavailable)
    /// </summary>
    public int BlockStatusCode { get; set; } = 403;

    /// <summary>
    ///     Message to return in response body when blocking bots.
    /// </summary>
    public string BlockMessage { get; set; } = "Access denied";

    /// <summary>
    ///     Minimum confidence score required to block (when BlockDetectedBots is true).
    ///     Set higher than BotThreshold for conservative blocking.
    ///     Valid range: 0.0 to 1.0
    /// </summary>
    public double MinConfidenceToBlock { get; set; } = 0.8;

    /// <summary>
    ///     Allow verified search engine bots (Googlebot, Bingbot, etc.) through even when blocking.
    ///     Recommended: true, unless you have specific SEO requirements.
    /// </summary>
    public bool AllowVerifiedSearchEngines { get; set; } = true;

    /// <summary>
    ///     Allow social media preview bots (Facebook, Twitter, LinkedIn, etc.) through.
    /// </summary>
    public bool AllowSocialMediaBots { get; set; } = true;

    /// <summary>
    ///     Allow monitoring bots (UptimeRobot, Pingdom, etc.) through.
    /// </summary>
    public bool AllowMonitoringBots { get; set; } = true;

    /// <summary>
    ///     Allow developer HTTP tools (curl, wget, httpie, python-requests, etc.) through even when blocking.
    ///     Tools are still subject to rate limiting via action policies (throttle-stealth, etc.).
    ///     Default is false.
    /// </summary>
    public bool AllowTools { get; set; }

    // ==========================================
    // Legacy Ollama Settings (use AiDetection section instead)
    // ==========================================
    // These are kept for backwards compatibility.
    // New deployments should use AiDetection section.

    /// <summary>
    ///     [OBSOLETE] Use AiDetection.Ollama.Endpoint instead.
    ///     This property will be removed in a future version.
    /// </summary>
    [Obsolete("Use AiDetection.Ollama.Endpoint instead. This property will be removed in v1.0.")]
    public string? OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    ///     [OBSOLETE] Use AiDetection.Ollama.Model instead.
    ///     This property will be removed in a future version.
    /// </summary>
    [Obsolete("Use AiDetection.Ollama.Model instead. This property will be removed in v1.0.")]
    public string? OllamaModel { get; set; } = "qwen3:0.6b";

    /// <summary>
    ///     [OBSOLETE] Use AiDetection.TimeoutMs instead.
    ///     This property will be removed in a future version.
    /// </summary>
    [Obsolete("Use AiDetection.TimeoutMs instead. This property will be removed in v1.0.")]
    public int LlmTimeoutMs { get; set; } = 15000;

    /// <summary>
    ///     [OBSOLETE] Use AiDetection.MaxConcurrentRequests instead.
    ///     This property will be removed in a future version.
    /// </summary>
    [Obsolete("Use AiDetection.MaxConcurrentRequests instead. This property will be removed in v1.0.")]
    public int MaxConcurrentLlmRequests { get; set; } = 5;

    // ==========================================
    // Behavioral Analysis Settings
    // ==========================================

    /// <summary>
    ///     Maximum requests per IP per minute for behavioral analysis (default: 60)
    ///     Valid range: 1 to 10000
    ///     Requests above this threshold increase bot confidence score.
    /// </summary>
    public int MaxRequestsPerMinute { get; set; } = 60;

    /// <summary>
    ///     Time window for behavioral analysis in seconds.
    ///     Tracks request counts within this sliding window.
    /// </summary>
    public int BehavioralWindowSeconds { get; set; } = 60;

    /// <summary>
    ///     Advanced behavioral analysis configuration.
    ///     Enables tracking at multiple identity levels (fingerprint, API key, user ID).
    /// </summary>
    public BehavioralOptions Behavioral { get; set; } = new();

    /// <summary>
    ///     Anomaly saver configuration - writes bot detection events to rolling JSON files.
    ///     Disabled by default for privacy/storage reasons.
    /// </summary>
    public AnomalySaverOptions AnomalySaver { get; set; } = new();

    /// <summary>
    ///     Browser and OS version age detection configuration.
    ///     Detects bots using outdated or impossible browser/OS combinations.
    /// </summary>
    public VersionAgeOptions VersionAge { get; set; } = new();

    // ==========================================
    // Caching Settings
    // ==========================================

    /// <summary>
    ///     Cache duration for detection results in seconds (default: 300)
    ///     Valid range: 0 to 86400 (24 hours)
    ///     Set to 0 to disable caching (not recommended for production).
    /// </summary>
    public int CacheDurationSeconds { get; set; } = 300;

    /// <summary>
    ///     Maximum number of cached detection results.
    ///     Prevents memory exhaustion from cache growth.
    /// </summary>
    public int MaxCacheEntries { get; set; } = 10000;

    // ==========================================
    // Background Update Service Settings
    // ==========================================

    /// <summary>
    ///     Enable the background service that updates bot lists automatically.
    ///     When disabled, lists are only loaded once at startup.
    /// </summary>
    public bool EnableBackgroundUpdates { get; set; } = true;

    /// <summary>
    ///     DEPRECATED: Use UpdateSchedule instead.
    ///     Interval between bot list update checks in hours (default: 24)
    ///     Valid range: 1 to 168 (1 week)
    ///     Lists are only downloaded if they're older than this.
    /// </summary>
    [Obsolete("Use UpdateSchedule with cron expression instead. This will be removed in v2.0.")]
    public int UpdateIntervalHours { get; set; } = 24;

    /// <summary>
    ///     DEPRECATED: Use UpdateSchedule instead.
    ///     Interval between update check polls in minutes (default: 60)
    ///     The service checks if an update is needed at this interval.
    ///     Valid range: 5 to 1440 (24 hours)
    /// </summary>
    [Obsolete("Use UpdateSchedule with cron expression instead. This will be removed in v2.0.")]
    public int UpdateCheckIntervalMinutes { get; set; } = 60;

    /// <summary>
    ///     Schedule configuration for bot list updates using cron expressions.
    ///     Supports JSON configuration with cron, timezone, signal, key, and runOnStartup.
    ///     Example JSON:
    ///     {
    ///     "cron": "0 2 * * *",           // 2 AM daily
    ///     "timezone": "UTC",              // Optional, defaults to UTC
    ///     "signal": "botlist.update",     // Signal to emit when update runs
    ///     "key": "scheduled",             // Optional key for tracking
    ///     "runOnStartup": true,           // Run immediately on startup
    ///     "description": "Daily bot list refresh"
    ///     }
    ///     Common cron patterns:
    ///     - "0 */6 * * *"  → Every 6 hours
    ///     - "0 2 * * *"    → Daily at 2 AM
    ///     - "0 2 * * 0"    → Weekly on Sunday at 2 AM
    ///     - "0 2 1 * *"    → Monthly on 1st at 2 AM
    /// </summary>
    public ListUpdateScheduleOptions? UpdateSchedule { get; set; } = new()
    {
        Cron = "0 2 * * *", // Default: Daily at 2 AM UTC
        Timezone = "UTC",
        Signal = "botlist.update",
        RunOnStartup = true,
        Description = "Daily bot list update"
    };

    /// <summary>
    ///     Timeout for downloading bot lists in seconds (default: 30)
    ///     If exceeded, the download fails gracefully and retries later.
    /// </summary>
    public int ListDownloadTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum retries for failed list downloads before giving up until next interval.
    /// </summary>
    public int MaxDownloadRetries { get; set; } = 3;

    /// <summary>
    ///     Delay startup initialization to avoid slowing app startup.
    ///     Lists will be loaded after this delay.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 5;

    // ==========================================
    // External Data Sources Configuration
    // ==========================================

    /// <summary>
    ///     Configuration for all external data sources.
    ///     Each source can be individually enabled/disabled with custom URLs.
    ///     See DataSourceOptions for default URLs and documentation.
    /// </summary>
    public DataSourcesOptions DataSources { get; set; } = new();

    // ==========================================
    // Fast Path / Signal-Driven Detection Settings
    // ==========================================

    /// <summary>
    ///     Configuration for fast-path detection with signal-driven architecture.
    /// </summary>
    public FastPathOptions FastPath { get; set; } = new();

    // ==========================================
    // Pattern Reputation Settings (Learning + Forgetting)
    // ==========================================

    /// <summary>
    ///     Configuration for pattern reputation tracking.
    ///     Controls how patterns are learned, forgotten, and used in detection.
    ///     Implements principled forgetting via time decay and evidence-based updates.
    /// </summary>
    public ReputationOptions Reputation { get; set; } = new();

    // ==========================================
    // Blackboard Orchestrator Settings
    // ==========================================

    /// <summary>
    ///     Configuration for the blackboard orchestrator.
    ///     Controls wave-based parallel execution, circuit breakers, and resilience.
    /// </summary>
    public OrchestratorOptions Orchestrator { get; set; } = new();

    /// <summary>
    ///     Configuration for the cross-request signature coordinator.
    ///     Tracks signatures across multiple requests to detect aberrant behavior patterns.
    /// </summary>
    public SignatureCoordinatorOptions SignatureCoordinator { get; set; } = new();

    /// <summary>
    ///     Configuration for response detection and coordination.
    ///     Enables cross-request response analysis and signature-level learning.
    /// </summary>
    public ResponseCoordinatorOptions ResponseCoordinator { get; set; } = new();

    /// <summary>
    ///     Configuration for bot cluster detection (label propagation clustering).
    /// </summary>
    public ClusterOptions Cluster { get; set; } = new();

    /// <summary>
    ///     Configuration for per-country bot rate tracking with decay.
    /// </summary>
    public CountryReputationOptions CountryReputation { get; set; } = new();

    /// <summary>
    ///     Configuration for signature convergence (merge/split families).
    ///     Detects IP-level relationships across signatures and groups them.
    /// </summary>
    public SignatureConvergenceOptions SignatureConvergence { get; set; } = new();

    // ==========================================
    // Pattern Learning Settings (Legacy - use Reputation instead)
    // ==========================================

    /// <summary>
    ///     Enable learning and storing new bot patterns.
    ///     Patterns are learned from requests with high confidence scores.
    /// </summary>
    public bool EnablePatternLearning { get; set; } = false;

    /// <summary>
    ///     Minimum confidence score to learn a pattern.
    ///     Only requests above this threshold contribute to learning.
    /// </summary>
    public double MinConfidenceToLearn { get; set; } = 0.9;

    /// <summary>
    ///     Maximum number of learned patterns to store.
    ///     Oldest patterns are removed when limit is reached.
    /// </summary>
    public int MaxLearnedPatterns { get; set; } = 1000;

    /// <summary>
    ///     Interval for consolidating/cleaning learned patterns in hours.
    ///     Removes low-value and duplicate patterns.
    /// </summary>
    public int PatternConsolidationIntervalHours { get; set; } = 24;

    // ==========================================
    // Storage Settings
    // ==========================================

    /// <summary>
    ///     Storage provider for bot patterns and IP ranges.
    ///     - PostgreSQL: Recommended for production when connection string is provided
    ///     - Sqlite: Fast, good for single-node deployments (default fallback)
    ///     - Json: Simple file-based, useful for debugging
    ///     NOTE: When PostgreSQLConnectionString is set, PostgreSQL is used automatically.
    /// </summary>
    public StorageProvider StorageProvider { get; set; } = StorageProvider.Sqlite;

    /// <summary>
    ///     PostgreSQL connection string for bot detection data storage.
    ///     When set, automatically enables PostgreSQL as the storage provider.
    ///     Takes precedence over SQLite - PostgreSQL is always preferred when available.
    ///     Example: "Host=localhost;Database=botdetection;Username=user;Password=pass"
    /// </summary>
    public string? PostgreSQLConnectionString { get; set; }

    /// <summary>
    ///     Path to the storage file (SQLite database or JSON file).
    ///     Only used when StorageProvider is Sqlite or Json.
    ///     Default for SQLite: {AppContext.BaseDirectory}/botdetection.db
    ///     Default for JSON: {AppContext.BaseDirectory}/botdetection.json
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    ///     Qdrant vector database configuration for similarity search.
    ///     When enabled, replaces the file-backed HNSW index with Qdrant.
    /// </summary>
    public QdrantOptions Qdrant { get; set; } = new();

    /// <summary>
    ///     Enable database WAL mode for better concurrent access (SQLite only).
    ///     Recommended for production.
    /// </summary>
    public bool EnableDatabaseWalMode { get; set; } = true;

    /// <summary>
    ///     Maximum number of weight entries to cache in memory (LRU cache).
    ///     This enables high-read/low-write access pattern (CQRS-style).
    ///     Reads hit memory cache, writes go to SQLite and update cache.
    ///     Default: 1000 entries per signature type.
    /// </summary>
    public int WeightStoreCacheSize { get; set; } = 1000;

    // ==========================================
    // Whitelists and Customization
    // ==========================================

    /// <summary>
    ///     Known good bot patterns (won't be flagged even if other signals present)
    /// </summary>
    public List<string> WhitelistedBotPatterns { get; set; } =
    [
        "Googlebot", "Bingbot", "Slackbot", "DuckDuckBot", "Baiduspider",
        "YandexBot", "Sogou", "Exabot", "facebot", "ia_archiver"
    ];

    /// <summary>
    ///     Known datacenter/hosting IP ranges (CIDR notation, increases suspicion)
    /// </summary>
    public List<string> DatacenterIpPrefixes { get; set; } =
    [
        "3.0.0.0/8", "13.0.0.0/8", "18.0.0.0/8", "52.0.0.0/8", // AWS
        "20.0.0.0/8", "40.0.0.0/8", "104.0.0.0/8", // Azure
        "34.0.0.0/8", "35.0.0.0/8", // GCP
        "138.0.0.0/8", "139.0.0.0/8", "140.0.0.0/8" // Oracle Cloud
    ];

    /// <summary>
    ///     Custom bot patterns to add to detection (regex patterns).
    /// </summary>
    public List<string> CustomBotPatterns { get; set; } = [];

    /// <summary>
    ///     IP addresses or CIDR ranges to always allow (bypass detection).
    /// </summary>
    public List<string> WhitelistedIps { get; set; } = [];

    /// <summary>
    ///     IP addresses or CIDR ranges to always block.
    /// </summary>
    public List<string> BlacklistedIps { get; set; } = [];

    // ==========================================
    // Logging Settings
    // ==========================================

    /// <summary>
    ///     Log all detection results (not just bots).
    ///     Useful for debugging but can be verbose.
    /// </summary>
    public bool LogAllRequests { get; set; } = false;

    /// <summary>
    ///     Log detailed detection reasons.
    /// </summary>
    public bool LogDetailedReasons { get; set; } = true;

    /// <summary>
    ///     Log performance metrics (processing time, cache hits, etc.)
    /// </summary>
    public bool LogPerformanceMetrics { get; set; } = false;

    /// <summary>
    ///     Log IP addresses in logs (disable for privacy compliance).
    /// </summary>
    public bool LogIpAddresses { get; set; } = true;

    /// <summary>
    ///     Log user agent strings in logs (disable for privacy compliance).
    /// </summary>
    public bool LogUserAgents { get; set; } = true;

    // ==========================================
    // Client-Side Detection Settings
    // ==========================================

    /// <summary>
    ///     Configuration for client-side browser fingerprinting.
    ///     Enables JavaScript-based headless browser and automation detection.
    /// </summary>
    public ClientSideOptions ClientSide { get; set; } = new();

    // ==========================================
    // Security Detection Settings
    // ==========================================

    /// <summary>
    ///     Configuration for detecting security/penetration testing tools.
    ///     Identifies vulnerability scanners, exploit frameworks, and hacking tools.
    ///     Part of the security detection layer for API honeypot integration.
    /// </summary>
    public SecurityToolOptions SecurityTools { get; set; } = new();

    /// <summary>
    ///     Configuration for Project Honeypot HTTP:BL integration.
    ///     Uses DNS lookups to check IP reputation against Project Honeypot's database.
    ///     Requires a free API key from https://www.projecthoneypot.org/
    /// </summary>
    public ProjectHoneypotOptions ProjectHoneypot { get; set; } = new();

    // ==========================================
    // Global Enable/Disable
    // ==========================================

    /// <summary>
    ///     Master switch to enable/disable all bot detection.
    ///     When false, middleware passes through all requests without detection.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Configuration for training data export endpoints.
    ///     Controls access, rate limiting, and security for ML training data export.
    /// </summary>
    public TrainingEndpointsOptions TrainingEndpoints { get; set; } = new();

    /// <summary>
    ///     When true, trust upstream detection headers (X-Bot-Detected, X-Bot-Confidence, etc.)
    ///     from a reverse proxy like YARP. Skips re-running the full detector pipeline.
    ///     Background learning (signature tracking, LLM enqueue) still runs using forwarded results.
    ///     Default: false (gateway keeps this off; downstream website sets it to true).
    /// </summary>
    public bool TrustUpstreamDetection { get; set; }

    /// <summary>
    ///     Name of the header containing the HMAC signature from the upstream gateway.
    ///     When set alongside TrustUpstreamDetection, the middleware will verify
    ///     that upstream headers were signed by a trusted gateway using the shared secret.
    ///     Default: null (no signature verification — only use when backend is network-isolated).
    /// </summary>
    public string? UpstreamSignatureHeader { get; set; }

    /// <summary>
    ///     Shared secret (base64-encoded) for verifying HMAC signatures on upstream detection headers.
    ///     Generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    ///     Must match the secret configured on the gateway.
    /// </summary>
    [JsonIgnore]
    public string? UpstreamSignatureSecret { get; set; }

    /// <summary>
    ///     Maximum age (in seconds) for upstream HMAC signatures before they are rejected.
    ///     Prevents replay attacks. Set higher for environments with clock skew.
    ///     Default: 300 (5 minutes).
    /// </summary>
    public int UpstreamSignatureMaxAgeSeconds { get; set; } = 300;

    // ==========================================
    // Response Headers Configuration
    // ==========================================

    /// <summary>
    ///     Configuration for adding bot detection results to response headers.
    ///     Useful for debugging and client-side JavaScript integration.
    /// </summary>
    public ResponseHeadersOptions ResponseHeaders { get; set; } = new();

    // ==========================================
    // YARP Learning Mode Configuration
    // ==========================================

    /// <summary>
    ///     Configuration for YARP learning mode - collects training data in gateway scenarios.
    ///     Runs full detection pipeline (except LLM) and outputs comprehensive signatures.
    ///     WARNING: This is a TRAINING/DEBUGGING mode - NOT for production blocking!
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         YARP Learning Mode captures comprehensive bot signatures including:
    ///         <list type="bullet">
    ///             <item>All detector outputs with timing and contributions</item>
    ///             <item>Blackboard signals collected during detection</item>
    ///             <item>HTTP context (optional - may contain PII)</item>
    ///             <item>YARP routing information (cluster, destination)</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Enable via configuration to collect training data:
    ///         <code>
    ///         "BotDetection": {
    ///           "DefaultPolicyName": "yarp-learning"
    ///         },
    ///         "YarpLearningMode": {
    ///           "Enabled": true,
    ///           "OutputPath": "./yarp-learning-data",
    ///           "SamplingRate": 0.1
    ///         }
    ///         </code>
    ///     </para>
    ///     <para>
    ///         See YARP_LEARNING_MODE.md for comprehensive documentation.
    ///     </para>
    /// </remarks>
    public YarpLearningModeOptions YarpLearningMode { get; set; } = new();

    // ==========================================
    // Throttling Configuration
    // ==========================================

    /// <summary>
    ///     Configuration for throttling detected bots.
    ///     Includes jitter support to make throttling less detectable.
    /// </summary>
    public ThrottlingOptions Throttling { get; set; } = new();

    // ==========================================
    // Policy Configuration
    // ==========================================

    /// <summary>
    ///     Named detection policies for path-based escalation.
    ///     Each policy defines its own detection pipeline and thresholds.
    ///     Key = policy name, Value = policy configuration.
    /// </summary>
    /// <example>
    ///     JSON configuration (appsettings.json):
    ///     <code>
    ///     "Policies": {
    ///       "strict": {
    ///         "Description": "High-security detection",
    ///         "FastPath": ["UserAgent", "Header", "Ip"],
    ///         "SlowPath": ["Behavioral", "Inconsistency", "ClientSide"],
    ///         "ForceSlowPath": true,
    ///         "ActionPolicyName": "block-hard",
    ///         "Tags": ["high-security"]
    ///       }
    ///     }
    ///     </code>
    ///     Code configuration:
    ///     <code>
    ///     options.Policies["custom"] = new DetectionPolicyConfig
    ///     {
    ///         FastPath = new List&lt;string&gt; { "UserAgent", "Header" },
    ///         ActionPolicyName = "throttle"
    ///     };
    ///     </code>
    /// </example>
    public Dictionary<string, DetectionPolicyConfig> Policies { get; set; } = new();

    /// <summary>
    ///     Maps URL path patterns to policy names.
    ///     Supports glob patterns (e.g., "/api/*", "/login/**").
    ///     Key = path pattern, Value = policy name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         By default, common static asset paths are mapped to the "static" policy
    ///         to prevent false positives from webpack bundles and frontend resources.
    ///         The "static" policy uses minimal detection with very high thresholds.
    ///     </para>
    ///     <para>
    ///         To disable default static file mappings, set <see cref="UseDefaultStaticPathPolicies" /> to false.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     "PathPolicies": {
    ///       "/api/**": "api",
    ///       "/admin/**": "strict",
    ///       "/js/**": "static",
    ///       "/css/**": "static"
    ///     }
    ///     </code>
    /// </example>
    public Dictionary<string, string> PathPolicies { get; set; } = new();

    /// <summary>
    ///     Default policy name to use when no path matches.
    ///     If not specified, uses built-in "default" policy.
    /// </summary>
    public string? DefaultPolicyName { get; set; }

    /// <summary>
    ///     Whether to apply default path mappings for static assets (JS, CSS, images, fonts).
    ///     When true, paths like /js/**, /css/**, /images/**, etc. automatically use the "static" policy.
    ///     Set to false to disable and configure your own static file handling.
    ///     Default: true
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The "static" policy is very permissive to avoid false positives from:
    ///         <list type="bullet">
    ///             <item>Webpack-bundled JavaScript with hash filenames</item>
    ///             <item>CSS stylesheets loaded in parallel</item>
    ///             <item>Image sprites and font files</item>
    ///             <item>Rapid browser prefetch/preload requests</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Default static paths: /js/**, /css/**, /lib/**, /fonts/**, /images/**, /img/**,
    ///         /assets/**, /static/**, /_content/**, and common file extensions.
    ///     </para>
    /// </remarks>
    public bool UseDefaultStaticPathPolicies { get; set; } = true;

    /// <summary>
    ///     Enable automatic static asset detection based on file extensions.
    ///     When true, requests with static asset extensions (like .js, .css, .png, .jpg)
    ///     automatically use the "static" policy regardless of path.
    ///     This is MORE comprehensive than path-based detection.
    ///     Default: true
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the RECOMMENDED approach for static asset detection because:
    ///         <list type="bullet">
    ///             <item>Works regardless of URL structure (handles CDN URLs, hash-based names)</item>
    ///             <item>Catches all static assets even if not in conventional /static/ paths</item>
    ///             <item>More reliable than path patterns alone</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Default extensions: .js, .css, .png, .jpg, .jpeg, .gif, .svg, .ico, .woff, .woff2,
    ///         .ttf, .eot, .otf, .webp, .avif, .map, .json (for source maps/manifests)
    ///     </para>
    /// </remarks>
    public bool UseFileExtensionStaticDetection { get; set; } = true;

    /// <summary>
    ///     Custom file extensions to treat as static assets (in addition to defaults).
    ///     Include the dot (e.g., ".pdf", ".mp4", ".webm").
    ///     Case-insensitive matching.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "StaticAssetExtensions": [".pdf", ".mp4", ".webm", ".zip"]
    ///     </code>
    /// </example>
    public List<string> StaticAssetExtensions { get; set; } = new();

    /// <summary>
    ///     Enable automatic static asset detection based on Content-Type header.
    ///     When true, responses with static asset MIME types automatically apply "static" policy.
    ///     This is a fallback for cases where file extension isn't available.
    ///     Default: false (disabled by default as Content-Type is only available after response)
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         IMPORTANT: This only works if middleware can read response Content-Type.
    ///         Most effective when combined with UseFileExtensionStaticDetection.
    ///     </para>
    ///     <para>
    ///         Default MIME types: image/*, font/*, text/css, text/javascript,
    ///         application/javascript, application/json (source maps)
    ///     </para>
    /// </remarks>
    public bool UseContentTypeStaticDetection { get; set; } = false;

    /// <summary>
    ///     Custom MIME type prefixes to treat as static assets (in addition to defaults).
    ///     Use prefixes for pattern matching (e.g., "video/" matches all video types).
    ///     Case-insensitive matching.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "StaticAssetMimeTypes": ["video/", "audio/", "application/pdf"]
    ///     </code>
    /// </example>
    public List<string> StaticAssetMimeTypes { get; set; } = new();

    /// <summary>
    ///     Global detector weight overrides.
    ///     Applied to all policies unless overridden at policy level.
    ///     Key = detector name, Value = weight multiplier.
    /// </summary>
    public Dictionary<string, double> GlobalWeights { get; set; } = new();

    // ==========================================
    // Action Policy Configuration
    // ==========================================

    /// <summary>
    ///     Named action policies for composable response handling.
    ///     Action policies define HOW to respond (block, throttle, challenge, etc.)
    ///     and are separate from detection policies (WHAT to detect) for maximum composability.
    ///     Key = policy name, Value = policy configuration.
    /// </summary>
    /// <example>
    ///     JSON configuration (appsettings.json):
    ///     <code>
    ///     "ActionPolicies": {
    ///       "hardBlock": {
    ///         "Type": "Block",
    ///         "StatusCode": 403,
    ///         "Message": "Access denied",
    ///         "IncludeRiskScore": false
    ///       },
    ///       "softThrottle": {
    ///         "Type": "Throttle",
    ///         "BaseDelayMs": 500,
    ///         "MaxDelayMs": 5000,
    ///         "JitterPercent": 0.25,
    ///         "ScaleByRisk": true
    ///       },
    ///       "captcha": {
    ///         "Type": "Challenge",
    ///         "ChallengeType": "Captcha",
    ///         "ChallengeUrl": "/challenge",
    ///         "TokenValidityMinutes": 30
    ///       }
    ///     }
    ///     </code>
    ///     Code configuration:
    ///     <code>
    ///     services.AddBotDetection(options =>
    ///     {
    ///         options.ActionPolicies["myThrottle"] = new ActionPolicyConfig
    ///         {
    ///             Type = "Throttle",
    ///             BaseDelayMs = 1000,
    ///             JitterPercent = 0.5,
    ///             ScaleByRisk = true
    ///         };
    ///     });
    ///     </code>
    /// </example>
    /// <remarks>
    ///     Built-in action policies are available without configuration:
    ///     - block, block-hard, block-soft, block-debug
    ///     - throttle, throttle-gentle, throttle-moderate, throttle-aggressive, throttle-stealth
    ///     - challenge, challenge-captcha, challenge-js, challenge-pow
    ///     - redirect, redirect-honeypot, redirect-tarpit, redirect-error
    ///     - logonly, shadow, debug
    /// </remarks>
    public Dictionary<string, ActionPolicyConfig> ActionPolicies { get; set; } = new();

    /// <summary>
    ///     Default action policy name to use when detection triggers blocking.
    ///     If not specified, uses "block" (built-in 403 block).
    /// </summary>
    public string? DefaultActionPolicyName { get; set; }

    /// <summary>
    ///     Per-bot-type action policy overrides. When a bot is detected as a specific type,
    ///     the matching policy name is used instead of DefaultActionPolicyName.
    ///     Key is the BotType enum name (e.g., "Tool", "Scraper", "AiBot"), value is the policy name.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "BotTypeActionPolicies": {
    ///         "Tool": "throttle-tools",
    ///         "Scraper": "throttle-aggressive",
    ///         "AiBot": "block-soft"
    ///     }
    ///     </code>
    /// </example>
    public Dictionary<string, string> BotTypeActionPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool"] = "throttle-tools"
    };

    // ==========================================
    // Path Exclusions and Overrides
    // ==========================================

    /// <summary>
    ///     Paths to completely exclude from bot detection.
    ///     Requests to these paths skip detection entirely (no processing, no logging).
    ///     Supports prefix matching (e.g., "/health" matches "/health" and "/health/live").
    ///     Use for health checks, internal endpoints, or paths you know don't need protection.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "ExcludedPaths": ["/health", "/metrics", "/.well-known", "/favicon.ico"]
    ///     </code>
    /// </example>
    public List<string> ExcludedPaths { get; set; } = ["/health", "/metrics"];

    /// <summary>
    ///     Paths where only signature generation runs (no detection pipeline).
    ///     The visitor's signature is computed and stored in HttpContext.Items
    ///     so downstream middleware can look them up in the visitor cache,
    ///     but no detectors run and no detection events are broadcast.
    /// </summary>
    public List<string> SignatureOnlyPaths { get; set; } = [];

    /// <summary>
    ///     Path overrides that always allow requests through, even if detected as bots.
    ///     Detection still runs (for logging/analytics), but blocking is bypassed.
    ///     Useful for fixing false positives without disabling detection entirely.
    ///     Supports glob patterns (e.g., "/api/public/*", "/webhooks/**").
    /// </summary>
    /// <remarks>
    ///     Use this when:
    ///     <list type="bullet">
    ///         <item>An endpoint is incorrectly flagging legitimate traffic</item>
    ///         <item>You need to allow specific bots/automation for an endpoint</item>
    ///         <item>Third-party integrations are being blocked</item>
    ///     </list>
    ///     Detection results are still logged and available via HttpContext.Items,
    ///     so you can monitor the traffic and adjust detection rules.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     "PathOverrides": {
    ///       "/api/webhooks/*": "allow",
    ///       "/api/public/feed": "allow",
    ///       "/callback/oauth": "allow"
    ///     }
    ///     </code>
    /// </example>
    public Dictionary<string, string> PathOverrides { get; set; } = new();

    // ==========================================
    // Pack Architecture Settings (Ephemeral Integration)
    // ==========================================

    /// <summary>
    ///     Maximum number of signals to keep in the SignalSink.
    ///     Older signals are evicted when capacity is reached.
    ///     Default: 10000
    /// </summary>
    public int MaxSignalCapacity { get; set; } = 10000;

    /// <summary>
    ///     How long to retain signals in the SignalSink in minutes.
    ///     Signals older than this are cleaned up.
    ///     Default: 5 minutes
    /// </summary>
    public int SignalRetentionMinutes { get; set; } = 5;

    /// <summary>
    ///     Enable parallel detection execution within waves.
    ///     When true, detectors in the same wave run concurrently.
    ///     Default: true
    /// </summary>
    public bool ParallelDetection { get; set; } = true;

    /// <summary>
    ///     Enable quorum-based early exit.
    ///     When confidence exceeds threshold, skip remaining detectors.
    ///     Default: true
    /// </summary>
    public bool EnableQuorumExit { get; set; } = true;

    /// <summary>
    ///     Confidence threshold for quorum exit.
    ///     Detection stops early when this confidence is reached.
    ///     Default: 0.9
    /// </summary>
    public double QuorumConfidenceThreshold { get; set; } = 0.9;

    /// <summary>
    ///     Overall detection timeout in milliseconds.
    ///     Detection aborted if it takes longer than this.
    ///     Default: 30000ms (30 seconds)
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>
    ///     Confidence threshold for learning system.
    ///     Only high-confidence detections are used for training.
    ///     Default: 0.85
    /// </summary>
    public double LearningConfidenceThreshold { get; set; } = 0.85;

    /// <summary>
    ///     Salience threshold for escalation.
    ///     Signals above this threshold are escalated to persistent storage.
    ///     Default: 0.8
    /// </summary>
    public double EscalationSalienceThreshold { get; set; } = 0.8;
}

// ==========================================
// List Update Scheduler Configuration
// ==========================================

/// <summary>
///     Schedule configuration for bot list updates using cron expressions.
///     Follows Ephemeral scheduler pattern with ScheduledTasksAtom integration.
/// </summary>
/// <example>
///     JSON configuration (appsettings.json):
///     <code>
///     "BotDetection": {
///       "UpdateSchedule": {
///         "cron": "0 2 * * *",
///         "timezone": "UTC",
///         "signal": "botlist.update",
///         "key": "scheduled",
///         "runOnStartup": true,
///         "description": "Daily bot list refresh"
///       }
///     }
///     </code>
///     Code configuration:
///     <code>
///     services.Configure&lt;BotDetectionOptions&gt;(options =&gt;
///     {
///         options.UpdateSchedule = new ListUpdateScheduleOptions
///         {
///             Cron = "0 */6 * * *",  // Every 6 hours
///             Timezone = "America/New_York",
///             RunOnStartup = true
///         };
///     });
///     </code>
/// </example>
/// <remarks>
///     Common cron patterns:
///     - "0 2 * * *"      → Daily at 2 AM
///     - "0 */6 * * *"    → Every 6 hours
///     - "0 2 * * 0"      → Weekly on Sunday at 2 AM
///     - "0 2 1 * *"      → Monthly on 1st at 2 AM
///     - "*/30 * * * *"   → Every 30 minutes
///     Integration with Ephemeral:
///     - Uses ScheduledTasksAtom for cron-based scheduling
///     - Signals emitted on update completion for coordination
///     - Supports durable task pattern for long-running updates
/// </remarks>
public class ListUpdateScheduleOptions
{
    /// <summary>
    ///     Cron expression for update schedule.
    ///     Format: minute hour day month weekday
    ///     Default: "0 2 * * *" (daily at 2 AM UTC)
    /// </summary>
    /// <example>
    ///     "0 2 * * *"      → Daily at 2 AM
    ///     "0 */6 * * *"    → Every 6 hours
    ///     "0 2 * * 0"      → Weekly on Sunday at 2 AM
    ///     "*/30 * * * *"   → Every 30 minutes
    /// </example>
    public string Cron { get; set; } = "0 2 * * *";

    /// <summary>
    ///     Timezone for cron expression evaluation.
    ///     Default: "UTC"
    /// </summary>
    /// <example>
    ///     "UTC", "America/New_York", "Europe/London", "Asia/Tokyo"
    /// </example>
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    ///     Signal to emit when update completes (for coordination with other atoms).
    ///     Default: "botlist.update"
    /// </summary>
    /// <remarks>
    ///     Used by Ephemeral's SignalSink pattern to coordinate dependent tasks.
    ///     Example: Other atoms can subscribe to "botlist.update" signal to refresh caches.
    /// </remarks>
    public string Signal { get; set; } = "botlist.update";

    /// <summary>
    ///     Optional key for tracking this scheduled task.
    ///     If null, uses the signal as the key.
    ///     Default: null
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    ///     Whether to run the update immediately on application startup.
    ///     If false, waits for first cron trigger.
    ///     Default: true
    /// </summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    ///     Human-readable description of this scheduled task.
    ///     Used for logging and dashboard display.
    ///     Default: "Daily bot list update"
    /// </summary>
    public string Description { get; set; } = "Daily bot list update";

    /// <summary>
    ///     Maximum execution time for a single update run (in seconds).
    ///     If update takes longer, it will be cancelled and retried on next schedule.
    ///     Default: 300 (5 minutes)
    /// </summary>
    public int MaxExecutionSeconds { get; set; } = 300;

    /// <summary>
    ///     Whether to use durable task pattern for update execution.
    ///     When true, update progress is persisted and can resume after restart.
    ///     Default: false (updates are fast enough to run fully on each trigger)
    /// </summary>
    public bool UseDurableTask { get; set; } = false;
}

// ==========================================
// Client-Side Detection Configuration
// ==========================================

/// <summary>
///     Configuration for client-side browser fingerprinting and headless detection.
///     Uses a lightweight JavaScript snippet to collect browser signals.
/// </summary>
public class ClientSideOptions
{
    /// <summary>
    ///     Enable client-side browser fingerprinting.
    ///     When enabled, use the &lt;bot-detection-script /&gt; tag helper to inject the JS.
    ///     Default: false
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Secret key for signing browser tokens (like XSRF tokens).
    ///     Tokens are used to validate fingerprint submissions and prevent spoofing.
    ///     If not set, a random key is generated (tokens won't survive restarts).
    ///     Recommended: Set a stable secret in production.
    /// </summary>
    public string? TokenSecret { get; set; }

    /// <summary>
    ///     Token lifetime in seconds.
    ///     Fingerprint must be submitted within this time of page load.
    ///     Default: 300 (5 minutes)
    /// </summary>
    public int TokenLifetimeSeconds { get; set; } = 300;

    /// <summary>
    ///     How long to cache fingerprint results for correlation with requests.
    ///     Default: 1800 (30 minutes)
    /// </summary>
    public int FingerprintCacheDurationSeconds { get; set; } = 1800;

    /// <summary>
    ///     Client-side collection timeout in milliseconds.
    ///     Default: 5000 (5 seconds)
    /// </summary>
    public int CollectionTimeoutMs { get; set; } = 5000;

    /// <summary>
    ///     Collect WebGL fingerprint data (vendor, renderer).
    ///     Provides higher entropy but some consider it more invasive.
    ///     Default: true
    /// </summary>
    public bool CollectWebGL { get; set; } = true;

    /// <summary>
    ///     Collect canvas fingerprint hash.
    ///     Used for consistency checking, not full fingerprinting.
    ///     Default: true
    /// </summary>
    public bool CollectCanvas { get; set; } = true;

    /// <summary>
    ///     Collect audio context fingerprint.
    ///     Currently not implemented, reserved for future use.
    ///     Default: false
    /// </summary>
    public bool CollectAudio { get; set; } = false;

    /// <summary>
    ///     Minimum browser integrity score to consider "trusted".
    ///     Scores below this contribute to bot confidence.
    ///     Range: 0-100. Default: 70
    /// </summary>
    public int MinIntegrityScore { get; set; } = 70;

    /// <summary>
    ///     Headless likelihood threshold above which to flag as bot.
    ///     Range: 0.0-1.0. Default: 0.5
    /// </summary>
    public double HeadlessThreshold { get; set; } = 0.5;
}

// ==========================================
// Detection Path Configuration
// ==========================================

/// <summary>
///     Configuration for detection paths (fast synchronous vs slow async).
///     Fully configurable via JSON or code with sensible defaults.
/// </summary>
/// <remarks>
///     <para>
///         Architecture:
///         <code>
///         ┌─────────────────────────────────────────────────────────────────┐
///         │ FAST PATH (synchronous, blocking)                               │
///         │ Runs on request thread, fastest detectors first                 │
///         │ If confidence > threshold → early exit → trigger slow path      │
///         │ Emits minimal event to bus; optionally samples for full analysis│
///         └─────────────────────────────────────────────────────────────────┘
///                                      ↓ (async, non-blocking)
///         ┌─────────────────────────────────────────────────────────────────┐
///         │ SLOW PATH (event-driven, background service)                    │
///         │ AI/ML inference, pattern learning, model updates                │
///         │ Compares fast vs full results → detects drift → updates trust   │
///         │ Results stored for future fast-path improvements                │
///         └─────────────────────────────────────────────────────────────────┘
///         </code>
///     </para>
///     <para>
///         Detectors are organized into waves for parallel execution:
///         <list type="bullet">
///             <item><b>Wave 1</b> - Fast pattern matching (&lt;1ms): UserAgent, Header</item>
///             <item><b>Wave 2</b> - Lookups and state (1-10ms): IP, Behavioral, ClientSide</item>
///             <item><b>Wave 3</b> - Analysis (10-100ms): Inconsistency, Heuristic</item>
///             <item><b>Wave 4</b> - AI/LLM (&gt;100ms): LLM detector</item>
///         </list>
///         Detectors in the same wave run in parallel. Waves execute sequentially.
///     </para>
/// </remarks>
/// <example>
///     JSON configuration (appsettings.json):
///     <code>
///     "FastPath": {
///       "Enabled": true,
///       "MaxParallelDetectors": 4,
///       "FastPathTimeoutMs": 100,
///       "FastPathDetectors": [
///         { "Name": "UserAgent", "Signal": "UserAgentAnalyzed", "Wave": 1, "Weight": 1.0 },
///         { "Name": "Header", "Signal": "HeadersAnalyzed", "Wave": 1, "Weight": 1.0 },
///         { "Name": "IP", "Signal": "IpAnalyzed", "Wave": 2, "Weight": 1.2 },
///         { "Name": "Behavioral", "Signal": "BehaviorAnalyzed", "Wave": 2, "Weight": 1.5 }
///       ],
///       "SlowPathDetectors": [
///         { "Name": "Heuristic", "Signal": "HeuristicCompleted", "Wave": 3, "Weight": 2.0 },
///         { "Name": "LLM", "Signal": "LlmCompleted", "Wave": 4, "Weight": 2.5 }
///       ]
///     }
///     </code>
///     Code configuration:
///     <code>
///     options.FastPath.FastPathDetectors = new List&lt;DetectorConfig&gt;
///     {
///         new() { Name = "MyCustomDetector", Signal = "CustomSignal", Wave = 1, Weight = 2.0 },
///         new() { Name = "UserAgent", Signal = "UserAgentAnalyzed", Wave = 1 }
///     };
///     </code>
/// </example>
public class FastPathOptions
{
    /// <summary>
    ///     Enable the dual-path architecture.
    ///     When disabled, all detectors run synchronously on the request thread.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    // ==========================================
    // Parallelism Settings
    // ==========================================

    /// <summary>
    ///     Maximum number of detectors to run in parallel within a wave.
    ///     Higher values use more CPU but complete faster.
    ///     Set to 1 for sequential execution within waves.
    ///     Default: 4
    /// </summary>
    public int MaxParallelDetectors { get; set; } = 4;

    /// <summary>
    ///     Enable wave-based parallel execution.
    ///     When true, detectors in the same wave run in parallel.
    ///     When false, all detectors run sequentially by priority.
    ///     Default: true
    /// </summary>
    public bool EnableWaveParallelism { get; set; } = true;

    /// <summary>
    ///     Continue to next wave even if current wave has failures.
    ///     When false, detection stops on first wave failure.
    ///     Default: true
    /// </summary>
    public bool ContinueOnWaveFailure { get; set; } = true;

    /// <summary>
    ///     Per-wave timeout in milliseconds.
    ///     Each wave must complete within this time or remaining detectors are cancelled.
    ///     Default: 50ms
    /// </summary>
    public int WaveTimeoutMs { get; set; } = 50;

    // ==========================================
    // Abort / Short-Circuit Settings
    // ==========================================

    /// <summary>
    ///     Confidence threshold for aborting fast path (UA-only short-circuit).
    ///     If the first detector (typically UA) returns confidence >= this value,
    ///     skip remaining detectors and classify immediately.
    ///     Range: 0.0-1.0. Default: 0.95 (very high confidence only)
    /// </summary>
    public double AbortThreshold { get; set; } = 0.95;

    /// <summary>
    ///     Sample rate for full-path analysis on aborted requests.
    ///     Even when aborting early, this fraction of requests will be
    ///     queued for full 8-layer analysis in the slow path.
    ///     Used to detect when fast-path classification drifts from full analysis.
    ///     Range: 0.0-1.0. Default: 0.01 (1% of aborted requests)
    /// </summary>
    public double SampleRate { get; set; } = 0.01;

    /// <summary>
    ///     Confidence threshold for early exit from fast path.
    ///     If fast-path confidence exceeds this, skip remaining fast-path detectors
    ///     and immediately trigger slow path for async processing.
    ///     Range: 0.0-1.0. Default: 0.85
    /// </summary>
    public double EarlyExitThreshold { get; set; } = 0.85;

    /// <summary>
    ///     Confidence threshold below which to skip slow path entirely.
    ///     If fast-path confidence is below this (clearly human), no need for
    ///     AI confirmation or learning - save resources.
    ///     Range: 0.0-1.0. Default: 0.2
    /// </summary>
    public double SkipSlowPathThreshold { get; set; } = 0.2;

    /// <summary>
    ///     Confidence threshold to trigger slow-path processing.
    ///     Detections in the "grey zone" (above skip, below early-exit) are
    ///     sent to slow path for additional analysis.
    ///     Range: 0.0-1.0. Default: 0.5
    /// </summary>
    public double SlowPathTriggerThreshold { get; set; } = 0.5;

    /// <summary>
    ///     Timeout for fast-path consensus in milliseconds.
    ///     If all fast-path detectors don't report within this time, finalise anyway.
    ///     Default: 100ms (fast path should be very quick)
    /// </summary>
    public int FastPathTimeoutMs { get; set; } = 100;

    /// <summary>
    ///     Maximum events to queue for slow-path before dropping oldest.
    ///     Prevents memory issues if slow-path processing falls behind.
    ///     Default: 10000
    /// </summary>
    public int SlowPathQueueCapacity { get; set; } = 10_000;

    // ==========================================
    // Always-Full-Path Routes
    // ==========================================

    /// <summary>
    ///     Paths that always run full 8-layer detection, even if UA screams "bot".
    ///     Use for high-value endpoints where you want deep intelligence:
    ///     - Authentication endpoints (/login, /signup)
    ///     - Payment flows (/checkout, /api/payments)
    ///     - Admin endpoints (/admin/*)
    ///     - Data exports
    ///     Matched using StartsWith (case-insensitive).
    ///     Default: empty (no forced full-path routes)
    /// </summary>
    public List<string> AlwaysRunFullOnPaths { get; set; } = [];

    // ==========================================
    // Drift Detection Settings
    // ==========================================

    /// <summary>
    ///     Enable drift detection to catch when fast-path (UA-only) diverges from full analysis.
    ///     When enabled, the slow path compares sampled fast-path results against full 8-layer
    ///     results and emits UaPatternDriftDetected events when disagreement exceeds threshold.
    ///     Default: true
    /// </summary>
    public bool EnableDriftDetection { get; set; } = true;

    /// <summary>
    ///     Disagreement rate threshold for drift detection.
    ///     If fast-path and full-path disagree on more than this fraction of sampled requests,
    ///     a drift event is emitted suggesting reduced trust in that UA pattern.
    ///     Range: 0.0-1.0. Default: 0.005 (0.5% disagreement triggers alert)
    /// </summary>
    public double DriftThreshold { get; set; } = 0.005;

    /// <summary>
    ///     Minimum sample count before evaluating drift.
    ///     Drift detection waits until this many samples are collected for a UA pattern
    ///     before comparing fast vs full results. Prevents noisy alerts from small samples.
    ///     Default: 100
    /// </summary>
    public int MinSamplesForDrift { get; set; } = 100;

    /// <summary>
    ///     Time window for drift detection in hours.
    ///     Samples older than this are excluded from drift calculations.
    ///     Default: 24 hours
    /// </summary>
    public int DriftWindowHours { get; set; } = 24;

    // ==========================================
    // Feedback Loop Settings
    // ==========================================

    /// <summary>
    ///     Enable automatic feedback from slow path to fast path.
    ///     When enabled, newly discovered bot signatures (UA, IP, characteristics)
    ///     are fed back to faster layers so they can use that information.
    ///     Default: true
    /// </summary>
    public bool EnableFeedbackLoop { get; set; } = true;

    /// <summary>
    ///     Minimum confidence from slow-path analysis to trigger feedback.
    ///     Only patterns detected with confidence >= this value are fed back.
    ///     Range: 0.0-1.0. Default: 0.9
    /// </summary>
    public double FeedbackMinConfidence { get; set; } = 0.9;

    /// <summary>
    ///     Minimum occurrences of a pattern before feeding back to fast path.
    ///     Prevents one-off detections from polluting the fast-path rules.
    ///     Default: 5
    /// </summary>
    public int FeedbackMinOccurrences { get; set; } = 5;

    // ==========================================
    // Detector Configuration
    // ==========================================

    /// <summary>
    ///     Detectors that run on the FAST PATH (synchronous, in-request).
    ///     Detectors are grouped by Wave for parallel execution.
    ///     Within a wave, detectors run in parallel up to MaxParallelDetectors.
    ///     Early exit happens as soon as cumulative confidence exceeds threshold.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Default wave assignments:
    ///         <list type="bullet">
    ///             <item>Wave 1: UserAgent, Header (pattern matching, &lt;1ms)</item>
    ///             <item>Wave 2: IP, Behavioral, ClientSide (lookups, 1-10ms)</item>
    ///             <item>Wave 3: Inconsistency (cross-signal analysis, 10-50ms)</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         You can fully customize this list in JSON or code.
    ///         Set Wave to control parallelism grouping.
    ///         Set Weight to control influence on final score.
    ///         Set Priority for ordering within a wave.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     Custom detector configuration:
    ///     <code>
    ///     "FastPathDetectors": [
    ///       { "Name": "MyFastDetector", "Signal": "FastSignal", "Wave": 1, "Weight": 2.0, "Priority": 100 },
    ///       { "Name": "UserAgent", "Signal": "UserAgentAnalyzed", "Wave": 1, "Weight": 1.0 }
    ///     ]
    ///     </code>
    /// </example>
    public List<DetectorConfig> FastPathDetectors { get; set; } =
    [
        // Wave 1: Fast pattern matching (<1ms)
        new()
        {
            Name = "User-Agent Detector", Signal = "UserAgentAnalyzed", ExpectedLatencyMs = 0.1, Wave = 1,
            Category = "UserAgent"
        },
        new()
        {
            Name = "Header Detector", Signal = "HeadersAnalyzed", ExpectedLatencyMs = 0.1, Wave = 1, Category = "Header"
        },

        // Wave 2: Lookups and state (1-10ms)
        new() { Name = "IP Detector", Signal = "IpAnalyzed", ExpectedLatencyMs = 0.5, Wave = 2, Category = "Ip" },
        new()
        {
            Name = "Behavioral Detector", Signal = "BehaviourSampled", ExpectedLatencyMs = 1, Wave = 2,
            Category = "Behavioral"
        },
        new()
        {
            Name = "Client-Side Detector", Signal = "ClientFingerprintReceived", ExpectedLatencyMs = 1, Wave = 2,
            Category = "ClientSide"
        },

        // Wave 3: Cross-signal analysis (10-50ms)
        new()
        {
            Name = "Inconsistency Detector", Signal = "InconsistencyUpdated", ExpectedLatencyMs = 2, Wave = 3,
            Category = "Inconsistency"
        }
    ];

    /// <summary>
    ///     Detectors that run on the SLOW PATH (async, background service).
    ///     These run via the learning event bus, not on the request thread.
    ///     Results are stored and can improve fast-path accuracy over time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Triggered when:
    ///         <list type="bullet">
    ///             <item>Fast path exits early (high confidence detection)</item>
    ///             <item>Fast path confidence is in the "grey zone"</item>
    ///             <item>Configured signals are emitted (see <see cref="SlowPathTriggers" />)</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Default: Heuristic detector with learned weights.
    ///         Add custom detectors for specialized analysis.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     "SlowPathDetectors": [
    ///       { "Name": "Heuristic Detector", "Signal": "AiClassificationCompleted", "Wave": 1, "Weight": 2.0 },
    ///       { "Name": "CustomMLModel", "Signal": "CustomMLCompleted", "Wave": 1, "Weight": 1.5 }
    ///     ]
    ///     </code>
    /// </example>
    public List<DetectorConfig> SlowPathDetectors { get; set; } =
    [
        new()
        {
            Name = "Heuristic Detector", Signal = "HeuristicCompleted", ExpectedLatencyMs = 1, Wave = 1,
            Category = "Heuristic", Weight = 2.0
        }
    ];

    /// <summary>
    ///     Detectors that run on the AI PATH (escalation only).
    ///     These are expensive detectors that only run when explicitly escalated.
    ///     Typically used for uncertain cases or high-value endpoints.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         AI path detectors are triggered by:
    ///         <list type="bullet">
    ///             <item>Risk score exceeding <see cref="Policies.DetectionPolicyConfig.AiEscalationThreshold" /></item>
    ///             <item>Detection policy with <see cref="Policies.DetectionPolicyConfig.EscalateToAi" /> = true</item>
    ///             <item>Policy transition with Action = "EscalateToAi"</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Default: LLM detector for natural language analysis of request patterns.
    ///         Add custom AI detectors for specialized inference.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     "AiPathDetectors": [
    ///       { "Name": "LLM", "Signal": "LlmCompleted", "Wave": 1, "Weight": 2.5, "TimeoutMs": 15000 },
    ///       { "Name": "CustomAI", "Signal": "CustomAICompleted", "Wave": 1, "Weight": 2.0 }
    ///     ]
    ///     </code>
    /// </example>
    public List<DetectorConfig> AiPathDetectors { get; set; } =
    [
        new()
        {
            Name = "LLM Detector", Signal = "LlmClassificationCompleted", ExpectedLatencyMs = 100, Wave = 1,
            Category = "AI", Weight = 2.5, TimeoutMs = 15000
        }
    ];

    /// <summary>
    ///     Signals that trigger slow-path processing.
    ///     When these signals are emitted, the slow path is activated.
    ///     Default triggers:
    ///     - HighConfidenceDetection: fast path found a likely bot
    ///     - GreyZoneDetection: uncertain result, need AI confirmation
    ///     - PatternDiscovered: potential new bot pattern to learn
    /// </summary>
    public List<string> SlowPathTriggers { get; set; } =
    [
        "HighConfidenceDetection",
        "GreyZoneDetection",
        "PatternDiscovered",
        "InconsistencyDetected"
    ];

    // ==========================================
    // Signature Matching Configuration
    // ==========================================

    /// <summary>
    ///     Configuration for multi-factor signature matching weights.
    ///     Used by FastPathSignatureMatcher for first-hit detection with false positive prevention.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Signature factors (server-side only, available immediately):
    ///         <list type="bullet">
    ///             <item><b>Primary</b>: HMAC(IP + UA) - exact match required</item>
    ///             <item><b>IP</b>: HMAC(IP) - handles UA changes (browser updates)</item>
    ///             <item><b>UA</b>: HMAC(User-Agent) - handles IP changes (mobile, dynamic ISP)</item>
    ///             <item><b>IP Subnet</b>: HMAC(IP /24) - network-level grouping</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Client-side factors (via postback, not used for first-hit):
    ///         <list type="bullet">
    ///             <item><b>ClientSide</b>: HMAC(Canvas+WebGL+AudioContext) - browser fingerprint</item>
    ///             <item><b>Plugin</b>: HMAC(Plugins+Extensions+Fonts) - browser configuration</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Matching rules (priority order):
    ///         <list type="number">
    ///             <item>Primary match → 100% confidence (exact same IP+UA)</item>
    ///             <item>IP + UA both match → 100% confidence (equivalent to Primary)</item>
    ///             <item>2+ factors with combined weight ≥100% → MATCH</item>
    ///             <item>3+ factors with combined weight ≥80% → WEAK MATCH</item>
    ///             <item>Otherwise → NO MATCH (avoid false positives)</item>
    ///         </list>
    ///     </para>
    /// </remarks>
    /// <example>
    ///     JSON configuration (appsettings.json):
    ///     <code>
    ///     "FastPath": {
    ///       "SignatureMatching": {
    ///         "WeightPrimary": 100.0,
    ///         "WeightIp": 50.0,
    ///         "WeightUa": 50.0,
    ///         "WeightIpSubnet": 30.0,
    ///         "WeightClientSide": 80.0,
    ///         "WeightPlugin": 60.0,
    ///         "MinWeightForMatch": 100.0,
    ///         "MinWeightForWeakMatch": 80.0,
    ///         "MinFactorsForWeakMatch": 3
    ///       }
    ///     }
    ///     </code>
    ///     Code configuration:
    ///     <code>
    ///     options.FastPath.SignatureMatching.WeightIp = 60.0;  // Increase IP weight
    ///     options.FastPath.SignatureMatching.MinWeightForMatch = 120.0;  // Stricter matching
    ///     </code>
    /// </example>
    public SignatureMatchingOptions SignatureMatching { get; set; } = new();
}

/// <summary>
///     Configuration for an individual detector in a path.
///     Inherits common extensibility properties from BaseComponentConfig.
/// </summary>
/// <example>
///     JSON configuration:
///     <code>
///     "FastPathDetectors": [
///       {
///         "Name": "User-Agent Detector",
///         "Signal": "UserAgentAnalyzed",
///         "ExpectedLatencyMs": 0.1,
///         "Weight": 1.5,
///         "Tags": ["fast", "required"]
///       }
///     ]
///     </code>
/// </example>
public class DetectorConfig : BaseComponentConfig
{
    /// <summary>
    ///     Detector name (must match IDetector.Name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Signal emitted when this detector completes.
    ///     Used for event-driven coordination.
    /// </summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>
    ///     Expected latency in milliseconds.
    ///     Used for timeout calculation and ordering decisions.
    /// </summary>
    public double ExpectedLatencyMs { get; set; }

    /// <summary>
    ///     Weight for this detector's confidence in final score.
    ///     Higher weight = more influence on final decision.
    ///     Default: 1.0
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    ///     Minimum confidence from this detector to contribute to early exit.
    ///     Prevents a single noisy detector from triggering early exit.
    ///     Range: 0.0-1.0. Default: 0.3
    /// </summary>
    public double MinConfidenceToContribute { get; set; } = 0.3;

    /// <summary>
    ///     Category of the detector (e.g., "UserAgent", "Header", "Ip", "Behavioral", "AI").
    ///     Used for grouping and filtering detectors.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    ///     Wave number for parallel execution (1 = fast, 2 = slow, 3 = AI).
    ///     Detectors in the same wave run in parallel.
    ///     Default: 1
    /// </summary>
    public int Wave { get; set; } = 1;

    /// <summary>
    ///     Whether results from this detector can be cached.
    ///     Default: true
    /// </summary>
    public bool IsCacheable { get; set; } = true;

    /// <summary>
    ///     Timeout for this detector in milliseconds.
    ///     If not set, uses the policy timeout.
    /// </summary>
    public int? TimeoutMs { get; set; }
}

// ==========================================
// Signature Matching Configuration
// ==========================================

/// <summary>
///     Configuration for multi-factor signature matching weights.
///     Controls how different signature factors contribute to matching confidence
///     and prevents false positives through weighted scoring.
/// </summary>
/// <remarks>
///     <para>
///         Signature matching uses multi-factor weighted scoring to identify returning clients
///         while guarding against false positives (e.g., different users in same office).
///     </para>
///     <para>
///         <b>Server-Side Factors</b> (available immediately on first request):
///         <list type="bullet">
///             <item><b>Primary</b>: HMAC(IP + UA) - 100% weight - exact composite match</item>
///             <item><b>IP</b>: HMAC(IP) - 50% weight - handles UA changes (browser updates)</item>
///             <item><b>UA</b>: HMAC(User-Agent) - 50% weight - handles IP changes (mobile, dynamic ISP)</item>
///             <item><b>IP Subnet</b>: HMAC(IP /24) - 30% weight - network-level grouping</item>
///         </list>
///     </para>
///     <para>
///         <b>Client-Side Factors</b> (via postback after response, future matching):
///         <list type="bullet">
///             <item><b>ClientSide</b>: HMAC(Canvas+WebGL+AudioContext) - 80% weight - hardware fingerprint</item>
///             <item><b>Plugin</b>: HMAC(Plugins+Extensions+Fonts) - 60% weight - browser config</item>
///         </list>
///     </para>
///     <para>
///         <b>False Positive Prevention Rules</b>:
///         <list type="number">
///             <item>Primary match → 100% confidence (instant match)</item>
///             <item>IP + UA both match → 100% confidence (equivalent to Primary)</item>
///             <item>2+ factors with combined weight ≥MinWeightForMatch → MATCH</item>
///             <item>MinFactorsForWeakMatch+ factors with weight ≥MinWeightForWeakMatch → WEAK MATCH</item>
///             <item>Otherwise → NO MATCH (insufficient confidence, avoid false positives)</item>
///         </list>
///     </para>
/// </remarks>
/// <example>
///     <b>Example 1: Corporate Network (False Positive Prevention)</b>
///     <code>
///     Employee A (first request):
///       Primary: ABC123... (HMAC of 192.168.1.10 + Chrome/120)
///       IP: DEF456...
///       UA: GHI789...
/// 
///     Employee B (same office, same browser version):
///       Primary: ABC456... ← DIFFERENT (subtle UA variations)
///       IP: DEF456... ← SAME
///       UA: GHI789... ← SAME
/// 
///     Matching:
///       IP matches → +50% weight
///       UA matches → +50% weight
///       Total: 100% (2 factors)
/// 
///     BUT Primary is DIFFERENT → This means IP+UA composite differs
///     Decision: ⚠️ WEAK MATCH (require client-side postback to confirm)
///     </code>
///     <b>Example 2: Mobile User (IP Changes, Legitimate)</b>
///     <code>
///     Initial Request (WiFi):
///       Primary: ABC123...
///       IP: WiFi456...
///       UA: Mobile789...
/// 
///     Later Request (Cellular):
///       Primary: XYZ999... ← CHANGED
///       IP: Cell123... ← CHANGED
///       UA: Mobile789... ← SAME
/// 
///     Matching:
///       UA matches → +50% weight
///       Total: 50% (1 factor)
/// 
///     Decision: ❌ NO MATCH (insufficient confidence)
///     → Wait for client-side postback
///     → ClientSide fingerprint (Canvas) will be SAME
///     → Next request: UA + ClientSide = 130% weight → MATCH ✅
///     </code>
///     <b>JSON Configuration</b>:
///     <code>
///     "FastPath": {
///       "SignatureMatching": {
///         "WeightPrimary": 100.0,
///         "WeightIp": 50.0,
///         "WeightUa": 50.0,
///         "WeightIpSubnet": 30.0,
///         "WeightClientSide": 80.0,
///         "WeightPlugin": 60.0,
///         "MinWeightForMatch": 100.0,
///         "MinWeightForWeakMatch": 80.0,
///         "MinFactorsForWeakMatch": 3
///       }
///     }
///     </code>
/// </example>
public class SignatureMatchingOptions
{
    // ==========================================
    // Server-Side Factor Weights
    // ==========================================

    /// <summary>
    ///     Weight for Primary signature match (HMAC of IP + UA composite).
    ///     This is the strongest signal - exact match means same device, same network, same browser.
    ///     Default: 100.0 (instant 100% confidence match)
    /// </summary>
    public double WeightPrimary { get; set; } = 100.0;

    /// <summary>
    ///     Weight for IP signature match (HMAC of IP address only).
    ///     Handles scenarios where User-Agent changes (browser updates, version bumps).
    ///     Default: 50.0 (moderate weight)
    /// </summary>
    public double WeightIp { get; set; } = 50.0;

    /// <summary>
    ///     Weight for User-Agent signature match (HMAC of UA string only).
    ///     Handles scenarios where IP changes (mobile networks, dynamic ISP, VPN switching).
    ///     Default: 50.0 (moderate weight)
    /// </summary>
    public double WeightUa { get; set; } = 50.0;

    /// <summary>
    ///     Weight for IP Subnet signature match (HMAC of IP /24 subnet).
    ///     Provides network-level grouping for datacenter detection and organizational patterns.
    ///     Default: 30.0 (weak weight, used for confirmation not primary matching)
    /// </summary>
    public double WeightIpSubnet { get; set; } = 30.0;

    // ==========================================
    // Client-Side Factor Weights (Postback Only)
    // ==========================================

    /// <summary>
    ///     Weight for client-side browser fingerprint (HMAC of Canvas+WebGL+AudioContext).
    ///     This is hardware-level fingerprint, very stable across IP/UA changes.
    ///     Only available AFTER client-side postback completes.
    ///     Default: 80.0 (high weight, very stable signal)
    /// </summary>
    public double WeightClientSide { get; set; } = 80.0;

    /// <summary>
    ///     Weight for plugin/font signature (HMAC of installed plugins, extensions, fonts).
    ///     Browser configuration fingerprint, moderately stable.
    ///     Only available AFTER client-side postback completes.
    ///     Default: 60.0 (moderate-high weight)
    /// </summary>
    public double WeightPlugin { get; set; } = 60.0;

    // ==========================================
    // Matching Thresholds (False Positive Prevention)
    // ==========================================

    /// <summary>
    ///     Minimum combined weight required for a confident match.
    ///     Requires Primary match (100%) OR IP+UA both match (50%+50%=100%) OR
    ///     any combination of factors that sum to this threshold.
    ///     Default: 100.0 (require full confidence)
    /// </summary>
    public double MinWeightForMatch { get; set; } = 100.0;

    /// <summary>
    ///     Minimum combined weight for a weak match (requires MinFactorsForWeakMatch+ factors).
    ///     Used when multiple weaker signals align (e.g., IP + UA + Subnet = 130%, but Primary differs).
    ///     Guards against false positives by requiring multiple confirming factors.
    ///     Default: 80.0 (80% confidence with 3+ factors)
    /// </summary>
    public double MinWeightForWeakMatch { get; set; } = 80.0;

    /// <summary>
    ///     Minimum number of matching factors required to use MinWeightForWeakMatch threshold.
    ///     Prevents single weak factor from triggering a match (e.g., Subnet alone at 30%).
    ///     Requires multiple signals to align before accepting lower confidence.
    ///     Default: 3 (need at least 3 factors for weak match)
    /// </summary>
    public int MinFactorsForWeakMatch { get; set; } = 3;
}

// ==========================================
// Behavioral Analysis Configuration
// ==========================================

/// <summary>
///     Advanced configuration for behavioral analysis.
///     Enables tracking at multiple identity levels beyond IP address.
/// </summary>
public class BehavioralOptions
{
    /// <summary>
    ///     HTTP header name to extract API key from for per-API-key rate limiting.
    ///     Example: "X-Api-Key", "Authorization"
    ///     Leave empty to disable API key tracking.
    /// </summary>
    public string? ApiKeyHeader { get; set; }

    /// <summary>
    ///     Rate limit per API key per minute.
    ///     If 0, defaults to MaxRequestsPerMinute * 2.
    /// </summary>
    public int ApiKeyRateLimit { get; set; } = 0;

    /// <summary>
    ///     Claim name to extract user ID from for per-user rate limiting.
    ///     Example: "sub", "nameidentifier", "userId"
    ///     Used when User.Identity.IsAuthenticated is true.
    /// </summary>
    public string? UserIdClaim { get; set; }

    /// <summary>
    ///     HTTP header name to extract user ID from (fallback when not authenticated).
    ///     Example: "X-User-Id"
    ///     Leave empty to disable header-based user tracking.
    /// </summary>
    public string? UserIdHeader { get; set; }

    /// <summary>
    ///     Rate limit per authenticated user per minute.
    ///     If 0, defaults to MaxRequestsPerMinute * 3.
    /// </summary>
    public int UserRateLimit { get; set; } = 0;

    /// <summary>
    ///     Enable behavior anomaly detection (sudden request spikes, unusual path access).
    ///     Detects when an identity suddenly changes behavior patterns.
    ///     Default: true
    /// </summary>
    public bool EnableAnomalyDetection { get; set; } = true;

    /// <summary>
    ///     Threshold multiplier for detecting request spikes.
    ///     A spike is detected when current rate exceeds average * this multiplier.
    ///     Default: 5.0 (5x the normal rate)
    /// </summary>
    public double SpikeThresholdMultiplier { get; set; } = 5.0;

    /// <summary>
    ///     Threshold for new path access rate to consider anomalous.
    ///     Range: 0.0-1.0. If 80%+ of recent requests are to new paths, flag as anomaly.
    ///     Default: 0.8
    /// </summary>
    public double NewPathAnomalyThreshold { get; set; } = 0.8;

    /// <summary>
    ///     Analysis window length for behavioral pattern detection.
    ///     Determines how far back to look when analyzing request patterns, entropy, and sequences.
    ///     This translates to the ephemeral tracking length for pattern analysis.
    ///     Default: 15 minutes
    /// </summary>
    public TimeSpan AnalysisWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Enable advanced pattern detection (entropy analysis, Markov chains, time-series anomaly detection).
    ///     Requires MathNet.Numerics for statistical analysis.
    ///     Default: true
    /// </summary>
    public bool EnableAdvancedPatternDetection { get; set; } = true;

    /// <summary>
    ///     Minimum number of requests required before performing statistical pattern analysis.
    ///     Default: 10
    /// </summary>
    public int MinRequestsForPatternAnalysis { get; set; } = 10;

    /// <summary>
    ///     Salt for identity hashing in pattern analysis.
    ///     This allows sharing hashed behavioral data across deployments using the same salt.
    ///     IMPORTANT: Keep this secret! Changing it will reset all behavioral tracking.
    ///     Default: Random GUID (generated per deployment)
    /// </summary>
    public string IdentityHashSalt { get; set; } = Guid.NewGuid().ToString();
}

// ==========================================
// Anomaly Saver Configuration
// ==========================================

/// <summary>
///     Configuration for the anomaly saver - writes bot detection events to rolling JSON files.
///     Uses ephemeral batching atoms for efficient I/O and automatic backpressure handling.
/// </summary>
/// <remarks>
///     <para>
///         The anomaly saver is a background service that captures bot detection events
///         and writes them to timestamped JSON files for auditing, analysis, and ML training.
///     </para>
///     <para>
///         <b>Output Format:</b> Each line is a complete JSON object (newline-delimited JSON).
///         This format is easy to parse, stream-friendly, and compatible with most log analysis tools.
///     </para>
///     <para>
///         <b>Rolling Strategy:</b> Files are automatically rolled based on time interval or size.
///         Old files are automatically cleaned up based on retention policy.
///     </para>
///     <para>
///         <b>Performance:</b> Uses BatchingAtom to buffer events in memory and write in batches,
///         minimizing I/O overhead and preventing disk contention.
///     </para>
/// </remarks>
/// <example>
///     Example configuration in appsettings.json:
///     <code>
/// {
///   "BotDetection": {
///     "AnomalySaver": {
///       "Enabled": true,
///       "OutputPath": "./logs/bot-detections.jsonl",
///       "MinBotProbabilityThreshold": 0.7,
///       "BatchSize": 50,
///       "FlushInterval": "00:00:05",
///       "RollingInterval": "01:00:00",
///       "MaxFileSizeBytes": 10485760,
///       "RetentionDays": 30
///     }
///   }
/// }
/// </code>
///     Example output (one JSON object per line):
///     <code>
/// {"timestamp":"2025-12-08T20:00:00Z","requestId":"abc123","botProbability":0.85,"isBot":true,"riskBand":"High","userAgent":"curl/7.68.0","ipAddress":"192.168.1.100","path":"/api/data","processingTimeMs":123.45,"contributingDetectors":["UserAgent","Behavioral"],"categoryBreakdown":{...}}
/// {"timestamp":"2025-12-08T20:00:01Z","requestId":"def456","botProbability":0.92,"isBot":true,"riskBand":"VeryHigh","userAgent":"python-requests/2.28.0","ipAddress":"10.0.0.50","path":"/scrape","processingTimeMs":89.12,"contributingDetectors":["UserAgent","IP","Behavioral"],"categoryBreakdown":{...}}
/// </code>
/// </example>
public class AnomalySaverOptions
{
    /// <summary>
    ///     Enable or disable the anomaly saver background service.
    ///     When disabled, no detection events are written to files.
    ///     Default: false (opt-in for privacy/storage reasons)
    /// </summary>
    /// <remarks>
    ///     Enable this only when you need audit logs, analysis data, or ML training datasets.
    ///     Be aware that this will write request metadata (IP, User-Agent, paths) to disk.
    /// </remarks>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Output file path for detection events.
    ///     Supports absolute or relative paths. Parent directory will be created if it doesn't exist.
    ///     Files are automatically timestamped when rolled (e.g., "bot-detections-20251208-143022.jsonl").
    ///     Default: "./logs/bot-detections.jsonl"
    /// </summary>
    /// <remarks>
    ///     <b>File Format:</b> .jsonl extension is recommended (newline-delimited JSON).
    ///     Each line is a complete, valid JSON object for easy streaming and parsing.
    ///     <b>Recommended Paths:</b>
    ///     - Development: "./logs/bot-detections.jsonl"
    ///     - Production: "/var/log/botdetection/detections.jsonl" (Linux)
    ///     - Production: "C:\\Logs\\BotDetection\\detections.jsonl" (Windows)
    ///     - Docker: "/app/logs/bot-detections.jsonl" (mount volume)
    /// </remarks>
    public string OutputPath { get; set; } = "./logs/bot-detections.jsonl";

    /// <summary>
    ///     Minimum bot probability threshold for saving events.
    ///     Only detections with botProbability >= this value are written to file.
    ///     Range: 0.0 to 1.0
    ///     Default: 0.5 (save medium-confidence and above)
    /// </summary>
    /// <remarks>
    ///     <b>Recommended Values:</b>
    ///     - 0.0 = Save all detections (including likely humans) - useful for ML training with balanced dataset
    ///     - 0.5 = Save medium-confidence and above - good for auditing borderline cases
    ///     - 0.7 = Save high-confidence bots only - reduces file size, focuses on clear bot traffic
    ///     - 0.9 = Save very-high-confidence only - minimal storage, captures only obvious bots
    ///     Lower thresholds = more data for ML training, higher storage costs.
    ///     Higher thresholds = focused audit trail, lower storage costs.
    /// </remarks>
    public double MinBotProbabilityThreshold { get; set; } = 0.5;

    /// <summary>
    ///     Number of detection events to batch before writing to file.
    ///     Uses ephemeral BatchingAtom for efficient I/O.
    ///     Default: 50 events
    /// </summary>
    /// <remarks>
    ///     <b>Performance Tradeoff:</b>
    ///     - Larger batches = fewer file writes, better throughput, but longer delay before flush
    ///     - Smaller batches = more file writes, lower throughput, but near-realtime logging
    ///     <b>Recommended Values:</b>
    ///     - Low traffic (&lt;100 req/min): 10-25 events
    ///     - Medium traffic (100-1000 req/min): 50-100 events
    ///     - High traffic (&gt;1000 req/min): 100-500 events
    ///     Events are also flushed on FlushInterval timeout, so batch size is a maximum, not a requirement.
    /// </remarks>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    ///     Maximum time to wait before flushing batched events to file, even if batch is not full.
    ///     Ensures events don't sit in memory indefinitely during low traffic.
    ///     Default: 5 seconds
    /// </summary>
    /// <remarks>
    ///     <b>Use Cases:</b>
    ///     - Short interval (1-5s): Near-realtime logging, good for debugging and monitoring
    ///     - Medium interval (10-30s): Balanced approach for production
    ///     - Long interval (60s+): Maximize batching efficiency, acceptable for background analysis
    ///     Events are guaranteed to be written within this interval OR when batch size is reached,
    ///     whichever comes first.
    /// </remarks>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Time interval after which to roll (create new) file.
    ///     Files are timestamped when rolled (e.g., "bot-detections-20251208-143022.jsonl").
    ///     Default: 1 hour
    /// </summary>
    /// <remarks>
    ///     <b>Rolling Strategy:</b>
    ///     Files roll when EITHER the time interval elapses OR max file size is reached.
    ///     <b>Recommended Values:</b>
    ///     - Hourly (01:00:00): Good for high-traffic sites, manageable file sizes
    ///     - Daily (1.00:00:00): Common for production, aligns with log rotation tools
    ///     - Weekly (7.00:00:00): Low-traffic sites, reduces file proliferation
    ///     <b>File Naming:</b>
    ///     Original: "bot-detections.jsonl"
    ///     Rolled: "bot-detections-20251208-143022.jsonl" (timestamp = UTC)
    /// </remarks>
    public TimeSpan RollingInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     Maximum file size in bytes before rolling to a new file.
    ///     Set to 0 to disable size-based rolling (time-based only).
    ///     Default: 10 MB (10485760 bytes)
    /// </summary>
    /// <remarks>
    ///     <b>Size Guidelines:</b>
    ///     - 1 MB = ~5,000-10,000 detection events (depends on metadata size)
    ///     - 10 MB = ~50,000-100,000 detection events (Default)
    ///     - 100 MB = ~500,000-1,000,000 detection events
    ///     - 1 GB = ~5-10 million detection events
    ///     <b>Recommended Values:</b>
    ///     - Development: 1-10 MB (small, easy to inspect)
    ///     - Production: 10-100 MB (balance between file count and individual file size)
    ///     - High-traffic: 100 MB - 1 GB (minimize file proliferation)
    ///     - Disabled (0): Only roll on time interval
    ///     Files are rolled when size is exceeded, even if RollingInterval hasn't elapsed.
    /// </remarks>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    ///     Number of days to retain old rolled files before automatic deletion.
    ///     Set to 0 or negative to disable automatic cleanup (files kept indefinitely).
    ///     Default: 30 days
    /// </summary>
    /// <remarks>
    ///     <b>Storage Management:</b>
    ///     Old files are automatically deleted when a new file is rolled.
    ///     Files older than (DateTime.UtcNow - RetentionDays) are removed.
    ///     <b>Recommended Values:</b>
    ///     - 7 days: Short-term audit trail, minimal storage
    ///     - 30 days: Standard retention for compliance/debugging
    ///     - 90 days: Extended retention for trend analysis
    ///     - 365 days: Long-term retention for ML training datasets
    ///     - 0 or -1: No automatic cleanup (manual management required)
    ///     <b>WARNING:</b> Ensure sufficient disk space for the retention period.
    ///     Estimate: (events/day) * (avg_event_size_bytes) * RetentionDays
    ///     Example: 100K events/day * 200 bytes/event * 30 days ≈ 600 MB
    /// </remarks>
    public int RetentionDays { get; set; } = 30;
}

// ==========================================
// Storage Provider Configuration
// ==========================================

/// <summary>
///     Specifies the storage provider for bot patterns and IP ranges.
/// </summary>
public enum StorageProvider
{
    /// <summary>
    ///     PostgreSQL database storage (recommended for production).
    ///     Scalable, supports clustering, and integrates with TimescaleDB.
    ///     Requires PostgreSQL connection string.
    /// </summary>
    PostgreSQL,

    /// <summary>
    ///     SQLite database storage.
    ///     Fast indexed queries, good for single-node deployments.
    ///     File: botdetection.db
    /// </summary>
    Sqlite,

    /// <summary>
    ///     JSON file storage.
    ///     Simple, human-readable, good for debugging or small deployments.
    ///     Loads entire file into memory on each operation.
    ///     File: botdetection.json
    /// </summary>
    Json
}

// ==========================================
// AI Detection Configuration Classes
// ==========================================

/// <summary>
///     Specifies the AI provider for bot detection.
/// </summary>
public enum AiProvider
{
    /// <summary>
    ///     Use Ollama with a remote LLM server (optional plugin).
    ///     REQUIRES: Ollama server running, ~1-4GB RAM depending on model.
    ///     LATENCY: 50-500ms per request depending on model size.
    ///     USE WHEN: You need external LLM services with managed infrastructure.
    /// </summary>
    Ollama,

    /// <summary>
    ///     Use LLamaSharp with local quantized models (llama.cpp backend).
    ///     LIGHTWEIGHT: In-process, no external service, ~300-500MB memory.
    ///     QUALITY: Good reasoning on small models (Qwen 0.5B-0.6B).
    ///     LATENCY: 50-150ms per request on CPU, 10-50ms on GPU.
    ///     USE WHEN: You want minimal dependencies and fast local inference.
    /// </summary>
    LlamaSharp,

    /// <summary>
    ///     Use heuristic-based detection with learned weights.
    ///     LIGHTWEIGHT: No external dependencies, minimal resources, fast inference.
    ///     QUALITY: Good for common patterns, improves over time via learning.
    ///     LATENCY: &lt;1ms per request (extremely fast).
    ///     USE WHEN: You need fast, lightweight detection without external servers.
    ///     NOTE: Weights are learned and persisted to database for continuous improvement.
    /// </summary>
    Heuristic
}

/// <summary>
///     Configuration for AI-based bot detection.
///     Supports Ollama (LLM) or Heuristic (lightweight learned model).
/// </summary>
public class AiDetectionOptions
{
    /// <summary>
    ///     The AI provider to use for bot detection.
    ///     Default: Ollama (for backwards compatibility)
    /// </summary>
    public AiProvider Provider { get; set; } = AiProvider.Ollama;

    /// <summary>
    ///     Timeout for AI detection in milliseconds.
    ///     If exceeded, AI detection is skipped (fail-safe).
    ///     Valid range: 100 to 60000. Default: 15000ms (15s for cold start)
    ///     Note: First request may be slower while model loads into GPU memory.
    /// </summary>
    public int TimeoutMs { get; set; } = 15000;

    /// <summary>
    ///     Maximum concurrent AI requests.
    ///     Prevents overwhelming the AI backend.
    ///     Valid range: 1 to 100. Default: 5
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 5;

    /// <summary>
    ///     Ollama-specific configuration.
    ///     Only used when Provider is Ollama.
    /// </summary>
    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>
    ///     LLamaSharp-specific configuration (local llama.cpp inference).
    ///     Only used when Provider is LlamaSharp.
    /// </summary>
    public LlamaSharpOptions LlamaSharp { get; set; } = new();

    /// <summary>
    ///     Heuristic-specific configuration.
    ///     Used for lightweight, fast bot detection with learned weights.
    /// </summary>
    public HeuristicOptions Heuristic { get; set; } = new();
}

/// <summary>
///     Configuration for the background LLM classification coordinator.
///     Controls the bounded channel that queues detection snapshots for async LLM analysis.
/// </summary>
public class LlmCoordinatorOptions
{
    /// <summary>
    ///     Maximum number of pending LLM classification requests.
    ///     When full, oldest requests are dropped (BoundedChannelFullMode.DropOldest).
    ///     Default: 20
    /// </summary>
    public int ChannelCapacity { get; set; } = 20;

    /// <summary>
    ///     Minimum heuristic bot probability to enqueue for LLM analysis.
    ///     Detections below this threshold are considered clearly human and skipped.
    ///     Default: 0.3
    /// </summary>
    public double MinProbabilityToEnqueue { get; set; } = 0.3;

    /// <summary>
    ///     Maximum heuristic bot probability to enqueue for LLM analysis.
    ///     Detections above this threshold are considered clearly bot and don't need LLM confirmation.
    ///     Default: 0.85
    /// </summary>
    public double MaxProbabilityToEnqueue { get; set; } = 0.85;

    /// <summary>Base sampling rate for drift detection (low-risk approvals). Default: 0.05 (5%)</summary>
    public double BaseSampleRate { get; set; } = 0.05;

    /// <summary>Sampling rate for high-risk confirmation. Default: 0.1 (10%)</summary>
    public double HighRiskConfirmationRate { get; set; } = 0.1;

    /// <summary>Skip enqueue if TimescaleDB says conclusive AND last LLM run was within this window. Default: 1 hour</summary>
    public TimeSpan ConclusiveSkipWindow { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
///     Ollama-specific configuration for AI detection.
/// </summary>
public class OllamaOptions
{
    /// <summary>
    ///     Default compact prompt template for bot detection.
    ///     Optimized for minimal token usage with small models.
    ///     Uses strict JSON schema to prevent malformed output.
    ///     Receives TOML-formatted evidence from all prior detectors.
    ///     KEY (abbreviations in evidence):
    ///     - H=Heuristic(trained ML model), prob=bot probability from H
    ///     - Scores: negative=human-like, positive=bot-like
    ///     - prob: 0.0=definitely human, 1.0=definitely bot
    /// </summary>
    public const string DefaultPrompt = @"Bot detector. JSON only.
{REQUEST_INFO}
RULES(priority order):
1. prob<0.3→human (trust H model)
2. prob>0.7→bot (trust H model)
3. ua~bot/crawler/spider/scraper→bot
4. ua~curl/wget/python/headless/sqlmap→bot
5. referer+lang+cookies→human
6. Chrome/Firefox/Safari+hdrs≥10→human
7. unsure→human,conf=0.3
TYPE:scraper|searchengine|monitor|malicious|social|good|unknown
{""isBot"":false,""confidence"":0.8,""reasoning"":""..."",""botType"":""unknown""}";

    /// <summary>
    ///     Whether Ollama LLM detection is enabled.
    ///     Default: true (but requires Ollama to be running)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Ollama API endpoint URL.
    ///     Default: "http://localhost:11434"
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    ///     Ollama model to use for bot detection.
    ///     Default: "qwen3:0.6b" (0.6B params, fast inference, good classification)
    ///     Alternatives:
    ///     - "gemma3:4b" - Larger, better reasoning
    ///     - "qwen2.5:1.5b" - Good reasoning, slightly larger
    ///     - "phi3:mini" - Microsoft's small model
    ///     - "tinyllama" - Very small, basic classification
    /// </summary>
    public string Model { get; set; } = "qwen3:0.6b";

    /// <summary>
    ///     Whether to use JSON mode for structured output.
    ///     When true, uses Ollama's JSON mode for reliable parsing.
    ///     Default: true
    /// </summary>
    public bool UseJsonMode { get; set; } = true;

    /// <summary>
    ///     Custom system prompt for bot detection.
    ///     Use {REQUEST_INFO} as placeholder for the request data.
    ///     If empty, uses the default compact prompt optimized for small models.
    ///     Default prompt (~350 tokens) is designed for small models like qwen3:0.6b.
    /// </summary>
    public string? CustomPrompt { get; set; }

    /// <summary>Number of CPU threads for Ollama inference. Default: 4</summary>
    public int NumThreads { get; set; } = 4;
}

/// <summary>
///     LLamaSharp-specific configuration for local llama.cpp inference.
///     Uses quantized GGUF models for in-process LLM execution without external services.
/// </summary>
public class LlamaSharpOptions
{
    /// <summary>
    ///     Whether LLamaSharp LLM inference is enabled.
    ///     Default: true (no server required, runs in-process)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Path to the GGUF model file or Hugging Face model identifier.
    ///     Examples:
    ///     - "./models/qwen-0.5b-q4_k_m.gguf" (local file)
    ///     - "qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf" (HF auto-download)
    ///     Default: Uses auto-download from Hugging Face
    /// </summary>
    public string ModelPath { get; set; } = "Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>
    ///     Cache directory for downloaded models.
    ///     Default: ~/.cache/stylobot-models (or STYLOBOT_MODEL_CACHE env var)
    ///     If empty, uses system default LLamaSharp cache
    /// </summary>
    public string? ModelCacheDir { get; set; }

    /// <summary>
    ///     Maximum context size for inference (tokens).
    ///     Larger = more memory but allows longer prompts.
    ///     Default: 512 (sufficient for bot classification)
    /// </summary>
    public int ContextSize { get; set; } = 512;

    /// <summary>
    ///     Number of CPU threads for inference.
    ///     Default: All available cores (auto-detected)
    /// </summary>
    public int ThreadCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    ///     Temperature for generation (0.0 = deterministic, 1.0 = creative).
    ///     Lower = more reliable for classification.
    ///     Default: 0.1 (very low randomness for bot naming)
    /// </summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>
    ///     Maximum tokens to generate per inference.
    ///     Default: 150 (sufficient for bot name + description)
    /// </summary>
    public int MaxTokens { get; set; } = 150;

    /// <summary>
    ///     Timeout for inference in milliseconds.
    ///     If exceeded, synthesis fails gracefully (returns null).
    ///     Default: 10000ms (10 seconds, CPU-only inference)
    /// </summary>
    public int TimeoutMs { get; set; } = 10000;
}

/// <summary>
///     Heuristic-specific configuration for lightweight bot detection.
///     Uses a simple linear model (logistic regression) with learned weights.
///     Weights are persisted to the database and updated via learning feedback.
/// </summary>
public class HeuristicOptions
{
    /// <summary>
    ///     Whether heuristic detection is enabled.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Load learned weights from the WeightStore on startup.
    ///     When true, weights persist across restarts and improve over time.
    ///     When false, uses default weights only.
    ///     Default: true
    /// </summary>
    public bool LoadLearnedWeights { get; set; } = true;

    /// <summary>
    ///     Update weights based on detection feedback.
    ///     When enabled, the model improves as it processes more requests.
    ///     Default: true
    /// </summary>
    public bool EnableWeightLearning { get; set; } = true;

    /// <summary>
    ///     Minimum confidence threshold for learning updates.
    ///     Only detections with confidence >= this value contribute to weight updates.
    ///     Range: 0.0-1.0. Default: 0.8
    /// </summary>
    public double MinConfidenceForLearning { get; set; } = 0.8;

    /// <summary>
    ///     Learning rate for weight updates.
    ///     Higher values = faster adaptation but potentially less stable.
    ///     Range: 0.001-0.5. Default: 0.01
    /// </summary>
    public double LearningRate { get; set; } = 0.01;

    /// <summary>
    ///     How often to reload weights from the store (in minutes).
    ///     Set to 0 to disable periodic reloads (weights only load on startup).
    ///     Default: 60 (hourly)
    /// </summary>
    public int WeightReloadIntervalMinutes { get; set; } = 60;
}

// ==========================================
// Data Source Configuration Classes
// ==========================================

/// <summary>
///     Configuration for all external data sources used by bot detection.
///     Each source can be individually enabled/disabled with custom URLs.
/// </summary>
public class DataSourcesOptions
{
    // ==========================================
    // Bot Pattern Sources (User-Agent matching)
    // ==========================================

    /// <summary>
    ///     IsBot patterns - the most comprehensive bot pattern source.
    ///     Aggregates patterns from: crawler-user-agents, matomo, myip.ms, and more.
    ///     Enabled by default as the primary pattern source.
    /// </summary>
    public DataSourceConfig IsBot { get; set; } = new()
    {
        Enabled = true,
        Url = "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json",
        Description = "IsBot patterns from omrilotan/isbot - comprehensive bot regex patterns (JSON array)"
    };

    /// <summary>
    ///     Matomo Device Detector bot list.
    ///     Provides categorized bot patterns with metadata (name, category, url).
    ///     Disabled by default as isbot already incorporates these patterns.
    ///     Enable if you need bot category information.
    /// </summary>
    public DataSourceConfig Matomo { get; set; } = new()
    {
        Enabled = false,
        Url = "https://raw.githubusercontent.com/matomo-org/device-detector/master/regexes/bots.yml",
        Description = "Matomo Device Detector - categorized bot patterns with metadata (YAML)"
    };

    /// <summary>
    ///     Crawler User Agents list.
    ///     Community-maintained list with crawler URLs.
    ///     Disabled by default as isbot already incorporates these patterns.
    /// </summary>
    public DataSourceConfig CrawlerUserAgents { get; set; } = new()
    {
        Enabled = false,
        Url = "https://raw.githubusercontent.com/monperrus/crawler-user-agents/master/crawler-user-agents.json",
        Description = "Crawler User Agents - community-maintained crawler patterns (JSON)"
    };

    // ==========================================
    // IP Range Sources (Datacenter detection)
    // ==========================================

    /// <summary>
    ///     AWS IP ranges - official Amazon IP ranges.
    ///     Used to detect requests from AWS infrastructure.
    /// </summary>
    public DataSourceConfig AwsIpRanges { get; set; } = new()
    {
        Enabled = true,
        Url = "https://ip-ranges.amazonaws.com/ip-ranges.json",
        Description = "AWS IP ranges - official Amazon cloud IP ranges (JSON)"
    };

    /// <summary>
    ///     Google Cloud IP ranges - official GCP IP ranges.
    ///     Used to detect requests from Google Cloud infrastructure.
    /// </summary>
    public DataSourceConfig GcpIpRanges { get; set; } = new()
    {
        Enabled = true,
        Url = "https://www.gstatic.com/ipranges/cloud.json",
        Description = "Google Cloud IP ranges - official GCP IP ranges (JSON)"
    };

    /// <summary>
    ///     Azure IP ranges - official Microsoft Azure IP ranges.
    ///     Disabled by default as the download URL changes weekly.
    ///     You must manually update the URL from:
    ///     https://www.microsoft.com/en-us/download/details.aspx?id=56519
    /// </summary>
    public DataSourceConfig AzureIpRanges { get; set; } = new()
    {
        Enabled = false,
        Url = "",
        Description = "Azure IP ranges - URL changes weekly, requires manual update"
    };

    /// <summary>
    ///     Cloudflare IPv4 ranges - official Cloudflare IP ranges.
    ///     Can be used to identify traffic proxied through Cloudflare.
    /// </summary>
    public DataSourceConfig CloudflareIpv4 { get; set; } = new()
    {
        Enabled = true,
        Url = "https://www.cloudflare.com/ips-v4",
        Description = "Cloudflare IPv4 ranges - official Cloudflare IPs (text, one CIDR per line)"
    };

    /// <summary>
    ///     Cloudflare IPv6 ranges - official Cloudflare IP ranges.
    /// </summary>
    public DataSourceConfig CloudflareIpv6 { get; set; } = new()
    {
        Enabled = true,
        Url = "https://www.cloudflare.com/ips-v6",
        Description = "Cloudflare IPv6 ranges - official Cloudflare IPs (text, one CIDR per line)"
    };

    // ==========================================
    // Browser Version Sources (Age detection)
    // ==========================================

    /// <summary>
    ///     Browser version data from useragents.me API.
    ///     Provides latest versions of major browsers for age checking.
    ///     Bots often use outdated browser versions - this helps detect them.
    /// </summary>
    public DataSourceConfig BrowserVersions { get; set; } = new()
    {
        Enabled = true,
        Url = "https://www.browsers.fyi/api",
        Description = "Browser versions from browsers.fyi - current browser versions (JSON)"
    };

    // ==========================================
    // Security Tool Pattern Sources
    // ==========================================

    /// <summary>
    ///     Security scanner user agents from digininja/scanner_user_agents.
    ///     Contains user agents for tools like Nikto, Nessus, SQLMap, WPScan, etc.
    ///     Part of the security detection layer for API honeypot integration.
    /// </summary>
    public DataSourceConfig ScannerUserAgents { get; set; } = new()
    {
        Enabled = true,
        Url = "https://raw.githubusercontent.com/digininja/scanner_user_agents/main/list.json",
        Description = "Security scanner user agents from digininja - JSON format with tool metadata"
    };

    /// <summary>
    ///     OWASP CoreRuleSet scanner patterns.
    ///     Community-maintained list of security tool patterns used by ModSecurity.
    ///     Text format with one pattern per line.
    /// </summary>
    public DataSourceConfig CoreRuleSetScanners { get; set; } = new()
    {
        Enabled = true,
        Url = "https://raw.githubusercontent.com/coreruleset/coreruleset/main/rules/scanners-user-agents.data",
        Description = "OWASP CoreRuleSet scanner patterns - text format, one per line"
    };
}

/// <summary>
///     Configuration for browser and OS version age detection.
///     Detects outdated browser/OS versions commonly used by bots and scrapers.
/// </summary>
public class VersionAgeOptions
{
    /// <summary>
    ///     Enable version age detection.
    ///     When enabled, requests with outdated browser/OS versions are flagged.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Update interval for version data in hours.
    ///     Browser versions change frequently, so daily updates are recommended.
    ///     Default: 24 hours
    /// </summary>
    public int UpdateIntervalHours { get; set; } = 24;

    // ==========================================
    // Browser Version Settings
    // ==========================================

    /// <summary>
    ///     Enable browser version checking.
    ///     Default: true
    /// </summary>
    public bool CheckBrowserVersion { get; set; } = true;

    /// <summary>
    ///     Maximum browser major version age to consider "current".
    ///     Browsers older than (latest - MaxBrowserVersionAge) are flagged.
    ///     Default: 10 major versions (e.g., Chrome 130 flags Chrome 119 and older)
    /// </summary>
    public int MaxBrowserVersionAge { get; set; } = 10;

    /// <summary>
    ///     Confidence boost for severely outdated browsers (>20 versions behind).
    ///     High confidence - very suspicious, likely a bot.
    ///     Default: 0.4
    /// </summary>
    public double BrowserSeverelyOutdatedConfidence { get; set; } = 0.4;

    /// <summary>
    ///     Confidence boost for moderately outdated browsers (10-20 versions behind).
    ///     Default: 0.2
    /// </summary>
    public double BrowserModeratelyOutdatedConfidence { get; set; } = 0.2;

    /// <summary>
    ///     Confidence boost for slightly outdated browsers (5-10 versions behind).
    ///     Default: 0.1
    /// </summary>
    public double BrowserSlightlyOutdatedConfidence { get; set; } = 0.1;

    // ==========================================
    // OS Version Settings (lower weight - legitimate old OS usage exists)
    // ==========================================

    /// <summary>
    ///     Enable OS version checking.
    ///     Lower weight than browser checks since people legitimately use old OS versions.
    ///     Default: true
    /// </summary>
    public bool CheckOsVersion { get; set; } = true;

    /// <summary>
    ///     Confidence boost for ancient OS (Windows XP, very old Android, etc.).
    ///     These are extremely rare in legitimate traffic.
    ///     Default: 0.3
    /// </summary>
    public double OsAncientConfidence { get; set; } = 0.3;

    /// <summary>
    ///     Confidence boost for very old OS (Windows 7, old macOS).
    ///     Still used by some legitimate users, so lower weight.
    ///     Default: 0.1
    /// </summary>
    public double OsVeryOldConfidence { get; set; } = 0.1;

    /// <summary>
    ///     Confidence boost for old OS (Windows 8/8.1, older Android).
    ///     Common enough to be only slightly suspicious.
    ///     Default: 0.05
    /// </summary>
    public double OsOldConfidence { get; set; } = 0.05;

    // ==========================================
    // Combined Anomaly Detection
    // ==========================================

    /// <summary>
    ///     Extra confidence boost when BOTH browser AND OS are outdated.
    ///     This combination is very suspicious - suggests hardcoded UA.
    ///     Default: 0.15
    /// </summary>
    public double CombinedOutdatedBoost { get; set; } = 0.15;

    /// <summary>
    ///     Confidence boost for impossible combinations (e.g., Chrome 130 on Windows XP).
    ///     These indicate a fake/crafted User-Agent.
    ///     Default: 0.5
    /// </summary>
    public double ImpossibleCombinationConfidence { get; set; } = 0.5;

    // ==========================================
    // Fallback Data (used when API unavailable)
    // ==========================================

    /// <summary>
    ///     Fallback browser versions when external API is unavailable.
    ///     These are ONLY used when the service fails to fetch data from browsers.fyi API.
    ///     Configure via appsettings.json - DO NOT hardcode versions in code as they go stale.
    ///     Leave empty {} to disable version age detection when API is unavailable.
    ///     Example:
    ///     "FallbackBrowserVersions": {
    ///     "Chrome": 143,
    ///     "Firefox": 146
    ///     }
    ///     Or leave empty to disable fallback:
    ///     "FallbackBrowserVersions": {}
    /// </summary>
    public Dictionary<string, int> FallbackBrowserVersions { get; set; } = new();

    /// <summary>
    ///     OS version classifications for age detection.
    ///     Key: OS identifier, Value: age category (current, old, very_old, ancient)
    /// </summary>
    public Dictionary<string, string> OsAgeClassification { get; set; } = new()
    {
        // Windows
        ["Windows NT 10.0"] = "current", // Windows 10/11
        ["Windows NT 6.3"] = "old", // Windows 8.1
        ["Windows NT 6.2"] = "old", // Windows 8
        ["Windows NT 6.1"] = "very_old", // Windows 7
        ["Windows NT 6.0"] = "ancient", // Vista
        ["Windows NT 5.1"] = "ancient", // XP
        ["Windows NT 5.0"] = "ancient", // 2000

        // macOS (by version number)
        ["Mac OS X 14"] = "current", // Sonoma
        ["Mac OS X 13"] = "current", // Ventura
        ["Mac OS X 12"] = "old", // Monterey
        ["Mac OS X 11"] = "old", // Big Sur
        ["Mac OS X 10_15"] = "old", // Catalina
        ["Mac OS X 10_14"] = "very_old", // Mojave
        ["Mac OS X 10_13"] = "very_old", // High Sierra
        ["Mac OS X 10_12"] = "ancient", // Sierra and older

        // Android (major versions)
        ["Android 14"] = "current",
        ["Android 13"] = "current",
        ["Android 12"] = "old",
        ["Android 11"] = "old",
        ["Android 10"] = "very_old",
        ["Android 9"] = "very_old",
        ["Android 8"] = "ancient",
        ["Android 7"] = "ancient",
        ["Android 6"] = "ancient",
        ["Android 5"] = "ancient",
        ["Android 4"] = "ancient",

        // iOS (major versions)
        ["iOS 18"] = "current",
        ["iOS 17"] = "current",
        ["iOS 16"] = "old",
        ["iOS 15"] = "old",
        ["iOS 14"] = "very_old",
        ["iOS 13"] = "very_old",
        ["iOS 12"] = "ancient",

        // Linux (generally current, hard to determine age)
        ["Linux"] = "current"
    };

    /// <summary>
    ///     Minimum browser version requirements per OS.
    ///     Used to detect impossible combinations (e.g., Chrome 130 can't run on XP).
    ///     Key: OS pattern, Value: minimum Chrome-equivalent version that runs on that OS.
    /// </summary>
    public Dictionary<string, int> MinBrowserVersionByOs { get; set; } = new()
    {
        ["Windows NT 5"] = 49, // XP: Chrome stopped at 49
        ["Windows NT 6.0"] = 49, // Vista: Chrome stopped at 49
        ["Windows NT 6.1"] = 109, // Win7: Chrome stopped at 109
        ["Mac OS X 10_9"] = 65, // Mavericks: old Chrome limit
        ["Mac OS X 10_10"] = 87, // Yosemite
        ["Mac OS X 10_11"] = 103, // El Capitan
        ["Android 4"] = 42, // Very old Android
        ["Android 5"] = 81, // Lollipop
        ["iOS 10"] = 49, // Old iOS
        ["iOS 11"] = 65
    };
}

/// <summary>
///     Configuration for a single external data source.
/// </summary>
/// <example>
///     JSON configuration with per-source update schedule:
///     <code>
///     "DataSources": {
///       "IsBot": {
///         "Enabled": true,
///         "Url": "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json",
///         "Description": "IsBot patterns",
///         "UpdateSchedule": {
///           "cron": "0 3 * * *",
///           "timezone": "UTC",
///           "runOnStartup": true
///         }
///       }
///     }
///     </code>
/// </example>
public class DataSourceConfig
{
    /// <summary>
    ///     Whether this data source is enabled.
    ///     Disabled sources are not fetched.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     URL to fetch the data from.
    ///     Leave empty to disable fetching (uses fallback patterns).
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    ///     Human-readable description of this data source.
    ///     For documentation purposes.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    ///     Optional per-source update schedule.
    ///     If null, uses the global UpdateSchedule configuration.
    ///     If set, overrides global schedule for this specific data source.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Use Cases for Per-Source Schedules:</b>
    ///         <list type="bullet">
    ///             <item><b>Different update frequencies</b>: AWS IP ranges change more often than bot patterns</item>
    ///             <item><b>Different timezones</b>: Update European sources during EU off-peak hours</item>
    ///             <item><b>Reduced load</b>: Stagger updates across different times to avoid spikes</item>
    ///             <item><b>Critical sources</b>: Update security tool patterns more frequently</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         <b>Example Schedules:</b>
    ///         <code>
    ///         // Global: Daily at 2 AM UTC
    ///         "UpdateSchedule": { "cron": "0 2 * * *" }
    /// 
    ///         // AWS IPs: Every 6 hours (changes frequently)
    ///         "AwsIpRanges": {
    ///           "UpdateSchedule": { "cron": "0 */6 * * *" }
    ///         }
    /// 
    ///         // Security tools: Every 2 hours (critical)
    ///         "ScannerUserAgents": {
    ///           "UpdateSchedule": { "cron": "0 */2 * * *" }
    ///         }
    /// 
    ///         // Browser versions: Weekly (changes slowly)
    ///         "BrowserVersions": {
    ///           "UpdateSchedule": { "cron": "0 2 * * 0" }
    ///         }
    ///         </code>
    ///     </para>
    /// </remarks>
    public ListUpdateScheduleOptions? UpdateSchedule { get; set; }
}

/// <summary>
///     Configuration for bot cluster detection via label propagation.
/// </summary>
public class ClusterOptions
{
    /// <summary>How often to re-run clustering (seconds). Default: 30</summary>
    public int ClusterIntervalSeconds { get; set; } = 30;

    /// <summary>Minimum pairwise similarity to create an edge. Default: 0.75</summary>
    public double SimilarityThreshold { get; set; } = 0.75;

    /// <summary>Minimum cluster size to report. Default: 3</summary>
    public int MinClusterSize { get; set; } = 3;

    /// <summary>Max label propagation iterations. Default: 10</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>Behavioral similarity threshold for "Bot Product" classification. Default: 0.85</summary>
    public double ProductSimilarityThreshold { get; set; } = 0.85;

    /// <summary>Temporal density threshold for "Bot Network" classification. Default: 0.6</summary>
    public double NetworkTemporalDensityThreshold { get; set; } = 0.6;

    /// <summary>Minimum avg bot probability for a signature to enter clustering. Default: 0.5</summary>
    public double MinBotProbabilityForClustering { get; set; } = 0.5;

    /// <summary>Number of new bot detections that trigger an early clustering run. Default: 20</summary>
    public int MinBotDetectionsToTrigger { get; set; } = 20;

    /// <summary>Enable semantic embeddings in clustering. Default: true</summary>
    public bool EnableSemanticEmbeddings { get; set; } = true;

    /// <summary>Clustering algorithm: "leiden" or "label_propagation". Default: leiden</summary>
    public string Algorithm { get; set; } = "leiden";

    /// <summary>Leiden resolution parameter (higher = more/smaller clusters). Default: 1.0</summary>
    public double LeidenResolution { get; set; } = 1.0;

    /// <summary>Weight for semantic similarity vs heuristic (0-1). Default: 0.4</summary>
    public double SemanticWeight { get; set; } = 0.4;

    /// <summary>Enable LLM-generated cluster descriptions. Default: false</summary>
    public bool EnableLlmDescriptions { get; set; }

    /// <summary>LLM model for cluster descriptions. Default: qwen3:0.6b</summary>
    public string DescriptionModel { get; set; } = "qwen3:0.6b";

    /// <summary>Ollama endpoint for cluster descriptions. Uses the main AiDetection endpoint if empty.</summary>
    public string? DescriptionEndpoint { get; set; }
}

/// <summary>
///     Configuration for per-country bot rate tracking with exponential decay.
/// </summary>
public class CountryReputationOptions
{
    /// <summary>Decay time constant in hours. Default: 24 (halves in ~17h)</summary>
    public double DecayTauHours { get; set; } = 24;

    /// <summary>Minimum total detections before country rate is meaningful. Default: 10</summary>
    public int MinSampleSize { get; set; } = 10;
}

/// <summary>
///     Configuration for training data export endpoints.
///     Controls access, rate limiting, and security for ML training data export.
/// </summary>
public class TrainingEndpointsOptions
{
    /// <summary>
    ///     Enable or disable training endpoints entirely. Default: true.
    ///     When false, MapBotTrainingEndpoints() returns the group but all endpoints return 404.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Require an API key via X-Training-Api-Key header. Default: false.
    ///     When true, requests without a valid key receive 401.
    /// </summary>
    public bool RequireApiKey { get; set; }

    /// <summary>
    ///     Allowed API keys for training endpoint access. Checked against X-Training-Api-Key header.
    ///     Set via config or BOTDETECTION_TRAINING_API_KEYS (comma-separated).
    /// </summary>
    public List<string> ApiKeys { get; set; } = [];

    /// <summary>
    ///     Maximum requests per minute per client IP. Default: 30.
    ///     Applies a sliding window rate limit. 0 = no limit.
    /// </summary>
    public int RateLimitPerMinute { get; set; } = 30;

    /// <summary>
    ///     Maximum number of signatures returned by /export endpoint. Default: 10000.
    ///     Prevents runaway memory and bandwidth usage on large deployments.
    /// </summary>
    public int MaxExportRecords { get; set; } = 10_000;
}

/// <summary>
///     Validates BotDetectionOptions on startup.
///     Invalid configuration logs warnings but doesn't crash the app.
/// </summary>
public class BotDetectionOptionsValidator : IValidateOptions<BotDetectionOptions>
{
    public ValidateOptionsResult Validate(string? name, BotDetectionOptions options)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Critical validations (would cause runtime errors)
        if (options.BotThreshold < 0.0 || options.BotThreshold > 1.0)
            errors.Add($"BotThreshold must be between 0.0 and 1.0, got {options.BotThreshold}");

        if (options.AiDetection.TimeoutMs < 100 || options.AiDetection.TimeoutMs > 30000)
            errors.Add($"AiDetection.TimeoutMs must be between 100 and 30000, got {options.AiDetection.TimeoutMs}");

        if (options.MaxRequestsPerMinute < 1 || options.MaxRequestsPerMinute > 10000)
            errors.Add($"MaxRequestsPerMinute must be between 1 and 10000, got {options.MaxRequestsPerMinute}");

        if (options.CacheDurationSeconds < 0 || options.CacheDurationSeconds > 86400)
            errors.Add($"CacheDurationSeconds must be between 0 and 86400, got {options.CacheDurationSeconds}");

#pragma warning disable CS0618 // Type or member is obsolete
        if (options.UpdateIntervalHours < 1 || options.UpdateIntervalHours > 168)
            errors.Add($"UpdateIntervalHours must be between 1 and 168, got {options.UpdateIntervalHours}");

        if (options.UpdateCheckIntervalMinutes < 5 || options.UpdateCheckIntervalMinutes > 1440)
            errors.Add(
                $"UpdateCheckIntervalMinutes must be between 5 and 1440, got {options.UpdateCheckIntervalMinutes}");
#pragma warning restore CS0618 // Type or member is obsolete

        // Validate Ollama settings only when using Ollama provider
        if (options.EnableLlmDetection && options.AiDetection.Provider == AiProvider.Ollama)
        {
            if (string.IsNullOrWhiteSpace(options.AiDetection.Ollama.Endpoint))
                errors.Add("AiDetection.Ollama.Endpoint must be specified when using Ollama provider");

            if (string.IsNullOrWhiteSpace(options.AiDetection.Ollama.Model))
                errors.Add("AiDetection.Ollama.Model must be specified when using Ollama provider");
        }

        if (options.MinConfidenceToBlock < options.BotThreshold)
            warnings.Add(
                $"MinConfidenceToBlock ({options.MinConfidenceToBlock}) is less than BotThreshold ({options.BotThreshold}), this may cause unexpected blocking");

        // Upstream trust without HMAC allows header spoofing — warn but allow (network-isolated backends are a valid use case)
        if (options.TrustUpstreamDetection &&
            (string.IsNullOrEmpty(options.UpstreamSignatureHeader) || string.IsNullOrEmpty(options.UpstreamSignatureSecret)))
            warnings.Add(
                "TrustUpstreamDetection is enabled without HMAC signature verification (UpstreamSignatureHeader/Secret). " +
                "Any client can forge X-Bot-Detected headers. Configure HMAC unless backend is network-isolated.");

        // Validate BehavioralOptions
        ValidateBehavioralOptions(options.Behavioral, errors, warnings);

        // Validate ClientSideOptions
        ValidateClientSideOptions(options.ClientSide, errors, warnings);

        // Validate CIDR patterns
        foreach (var prefix in options.DatacenterIpPrefixes)
            if (!IsValidCidr(prefix))
                errors.Add($"Invalid CIDR notation in DatacenterIpPrefixes: {prefix}");

        foreach (var ip in options.WhitelistedIps)
            if (!IsValidIpOrCidr(ip))
                errors.Add($"Invalid IP or CIDR in WhitelistedIps: {ip}");

        foreach (var ip in options.BlacklistedIps)
            if (!IsValidIpOrCidr(ip))
                errors.Add($"Invalid IP or CIDR in BlacklistedIps: {ip}");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static bool IsValidCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2) return false;

        if (!IPAddress.TryParse(parts[0], out _))
            return false;

        if (!int.TryParse(parts[1], out var prefix))
            return false;

        return prefix >= 0 && prefix <= 128;
    }

    private static bool IsValidIpOrCidr(string value)
    {
        if (value.Contains('/'))
            return IsValidCidr(value);

        return IPAddress.TryParse(value, out _);
    }

    private static void ValidateBehavioralOptions(BehavioralOptions options, List<string> errors, List<string> warnings)
    {
        // Rate limit validations
        if (options.ApiKeyRateLimit < 0)
            errors.Add($"Behavioral.ApiKeyRateLimit cannot be negative, got {options.ApiKeyRateLimit}");

        if (options.UserRateLimit < 0)
            errors.Add($"Behavioral.UserRateLimit cannot be negative, got {options.UserRateLimit}");

        // Spike threshold validation
        if (options.SpikeThresholdMultiplier < 1.0)
            errors.Add($"Behavioral.SpikeThresholdMultiplier must be >= 1.0, got {options.SpikeThresholdMultiplier}");

        if (options.SpikeThresholdMultiplier > 100.0)
            warnings.Add(
                $"Behavioral.SpikeThresholdMultiplier is very high ({options.SpikeThresholdMultiplier}), spike detection may be ineffective");

        // New path anomaly threshold validation
        if (options.NewPathAnomalyThreshold < 0.0 || options.NewPathAnomalyThreshold > 1.0)
            errors.Add(
                $"Behavioral.NewPathAnomalyThreshold must be between 0.0 and 1.0, got {options.NewPathAnomalyThreshold}");

        // Warn if API key header is set but rate limit is 0
        if (!string.IsNullOrEmpty(options.ApiKeyHeader) && options.ApiKeyRateLimit == 0)
            warnings.Add(
                "Behavioral.ApiKeyHeader is set but ApiKeyRateLimit is 0 (will use 2x MaxRequestsPerMinute as default)");

        // Warn if user ID is configured but rate limit is 0
        if ((!string.IsNullOrEmpty(options.UserIdHeader) || !string.IsNullOrEmpty(options.UserIdClaim)) &&
            options.UserRateLimit == 0)
            warnings.Add(
                "User ID tracking is configured but UserRateLimit is 0 (will use 3x MaxRequestsPerMinute as default)");
    }

    private static void ValidateClientSideOptions(ClientSideOptions options, List<string> errors, List<string> warnings)
    {
        if (!options.Enabled)
            return; // Skip validation if disabled

        // Token lifetime validation
        if (options.TokenLifetimeSeconds < 30)
            errors.Add($"ClientSide.TokenLifetimeSeconds must be >= 30 seconds, got {options.TokenLifetimeSeconds}");

        if (options.TokenLifetimeSeconds > 86400)
            warnings.Add(
                $"ClientSide.TokenLifetimeSeconds is very long ({options.TokenLifetimeSeconds}s), tokens may be reused inappropriately");

        // Fingerprint cache validation
        if (options.FingerprintCacheDurationSeconds < 0)
            errors.Add(
                $"ClientSide.FingerprintCacheDurationSeconds cannot be negative, got {options.FingerprintCacheDurationSeconds}");

        // Collection timeout validation
        if (options.CollectionTimeoutMs < 100)
            errors.Add($"ClientSide.CollectionTimeoutMs must be >= 100ms, got {options.CollectionTimeoutMs}");

        if (options.CollectionTimeoutMs > 30000)
            warnings.Add(
                $"ClientSide.CollectionTimeoutMs is very long ({options.CollectionTimeoutMs}ms), may affect user experience");

        // Integrity score validation
        if (options.MinIntegrityScore < 0 || options.MinIntegrityScore > 100)
            errors.Add($"ClientSide.MinIntegrityScore must be between 0 and 100, got {options.MinIntegrityScore}");

        // Headless threshold validation
        if (options.HeadlessThreshold < 0.0 || options.HeadlessThreshold > 1.0)
            errors.Add($"ClientSide.HeadlessThreshold must be between 0.0 and 1.0, got {options.HeadlessThreshold}");

        // Warn if no collection methods enabled
        if (!options.CollectWebGL && !options.CollectCanvas && !options.CollectAudio)
            warnings.Add(
                "ClientSide detection is enabled but all collection methods (WebGL, Canvas, Audio) are disabled");

        // Warn about production secret
        if (options.TokenSecret == "demo-secret-key-change-in-production" ||
            options.TokenSecret == "your-secret-key" ||
            options.TokenSecret?.Length < 16)
            warnings.Add(
                "ClientSide.TokenSecret should be a strong, unique secret in production (at least 16 characters)");
    }
}

// ==========================================
// Response Headers Configuration
// ==========================================

/// <summary>
///     Configuration for adding bot detection results to response headers.
///     Useful for debugging, client-side JavaScript integration, and third-party services.
/// </summary>
/// <remarks>
///     Example headers when enabled:
///     <code>
///     X-Bot-Risk-Score: 0.850
///     X-Bot-Risk-Band: High
///     X-Bot-Confidence: 0.920
///     X-Bot-Detectors: UserAgent,Header,Ip
///     X-Bot-Policy: strict
///     X-Bot-Processing-Ms: 12.5
///     </code>
/// </remarks>
public class ResponseHeadersOptions
{
    /// <summary>
    ///     Enable adding bot detection results to response headers.
    ///     Default: false (off for production, enable for debugging)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Prefix for all bot detection headers.
    ///     Default: "X-Bot-"
    ///     Example: With prefix "X-Bot-", headers are "X-Bot-Risk-Score", "X-Bot-Policy", etc.
    /// </summary>
    public string HeaderPrefix { get; set; } = "X-Bot-";

    /// <summary>
    ///     Include the policy name used for detection.
    ///     Header: {Prefix}Policy
    ///     Default: true
    /// </summary>
    public bool IncludePolicyName { get; set; } = true;

    /// <summary>
    ///     Include confidence score in headers.
    ///     Header: {Prefix}Confidence
    ///     Default: true
    /// </summary>
    public bool IncludeConfidence { get; set; } = true;

    /// <summary>
    ///     Include list of contributing detectors.
    ///     Header: {Prefix}Detectors (comma-separated)
    ///     Default: true
    /// </summary>
    public bool IncludeDetectors { get; set; } = true;

    /// <summary>
    ///     Include processing time in milliseconds.
    ///     Header: {Prefix}Processing-Ms
    ///     Default: true
    /// </summary>
    public bool IncludeProcessingTime { get; set; } = true;

    /// <summary>
    ///     Include detected bot name (if identified).
    ///     Header: {Prefix}Bot-Name
    ///     Default: false (may leak detection logic)
    /// </summary>
    public bool IncludeBotName { get; set; } = false;

    /// <summary>
    ///     Include full JSON result as Base64-encoded header.
    ///     Header: {Prefix}Result-Json (Base64 encoded)
    ///     Useful for client-side JavaScript to parse detection details.
    ///     Default: false (large header size)
    /// </summary>
    /// <remarks>
    ///     Decode in JavaScript:
    ///     <code>
    ///     const result = JSON.parse(atob(response.headers.get('X-Bot-Result-Json')));
    ///     </code>
    /// </remarks>
    public bool IncludeFullJson { get; set; } = false;

    /// <summary>
    ///     Paths to skip adding headers (e.g., health checks, metrics).
    ///     Uses prefix matching.
    ///     Default: ["/health", "/metrics", "/swagger"]
    /// </summary>
    public List<string> SkipPaths { get; set; } = ["/health", "/metrics", "/swagger"];
}

// ==========================================
// Throttling Configuration
// ==========================================

/// <summary>
///     Configuration for throttling detected bots.
///     Includes jitter support to make throttling less obvious to sophisticated bots.
/// </summary>
/// <remarks>
///     Jitter makes the throttling response time vary randomly, preventing bots from
///     easily detecting they're being throttled based on consistent response times.
///     Example with BaseDelaySeconds=60, JitterPercent=30:
///     - Min delay: 60 - 18 = 42 seconds
///     - Max delay: 60 + 18 = 78 seconds
///     - Each request gets a random value in this range
/// </remarks>
public class ThrottlingOptions
{
    /// <summary>
    ///     Base delay in seconds for Retry-After header.
    ///     Default: 60 seconds
    /// </summary>
    public int BaseDelaySeconds { get; set; } = 60;

    /// <summary>
    ///     Maximum delay in seconds (caps the delay after jitter and scaling).
    ///     Default: 300 seconds (5 minutes)
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 300;

    /// <summary>
    ///     Enable jitter to randomize the Retry-After value.
    ///     Makes it harder for bots to detect they're being throttled.
    ///     Default: true
    /// </summary>
    public bool EnableJitter { get; set; } = true;

    /// <summary>
    ///     Jitter range as a percentage of BaseDelaySeconds.
    ///     Example: 30 means ±30% of BaseDelaySeconds.
    ///     Valid range: 0-100. Default: 30
    /// </summary>
    public int JitterPercent { get; set; } = 30;

    /// <summary>
    ///     Scale the delay based on risk score.
    ///     Higher risk = longer delay.
    ///     Formula: delay = baseDelay * (1 + riskScore)
    ///     Default: false
    /// </summary>
    public bool ScaleByRisk { get; set; } = false;

    /// <summary>
    ///     Actually delay the response before sending 429.
    ///     This slows down the bot's request rate at the TCP level.
    ///     WARNING: Can consume server threads if many bots are hitting you.
    ///     Default: false
    /// </summary>
    public bool DelayResponse { get; set; } = false;

    /// <summary>
    ///     Response delay in milliseconds when DelayResponse is true.
    ///     Capped at 5000ms to prevent thread exhaustion.
    ///     Default: 1000ms (1 second)
    /// </summary>
    public int ResponseDelayMs { get; set; } = 1000;

    /// <summary>
    ///     Custom message to include in throttle response body.
    ///     Default: "Please slow down and try again later."
    /// </summary>
    public string ThrottleMessage { get; set; } = "Please slow down and try again later.";

    /// <summary>
    ///     URL to redirect blocked requests to (when using Redirect action).
    ///     Default: "/blocked"
    /// </summary>
    public string? BlockRedirectUrl { get; set; } = "/blocked";

    /// <summary>
    ///     Type of challenge to present (when using Challenge action).
    ///     Options: "captcha", "pow" (proof of work), "interstitial"
    ///     Default: "captcha"
    /// </summary>
    public string ChallengeType { get; set; } = "captcha";
}

// ==========================================
// Security Tool Detection Configuration
// ==========================================

/// <summary>
///     Configuration for detecting security/penetration testing tools.
///     Identifies vulnerability scanners, exploit frameworks, and hacking tools
///     based on User-Agent signatures.
///     Part of the security detection layer - designed to integrate with future
///     API honeypot systems (Mostlylucid.ApiHoneypot).
/// </summary>
public class SecurityToolOptions
{
    /// <summary>
    ///     Enable security tool detection.
    ///     When enabled, requests from known security tools are flagged as malicious.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Block requests from security tools immediately.
    ///     When false, tools are detected and logged but not blocked.
    ///     Default: true
    /// </summary>
    public bool BlockSecurityTools { get; set; } = true;

    /// <summary>
    ///     Log security tool detections at Warning level.
    ///     Useful for security monitoring and alerting.
    ///     Default: true
    /// </summary>
    public bool LogDetections { get; set; } = true;

    /// <summary>
    ///     Custom tool patterns to add to detection (case-insensitive substring match).
    ///     Use this to add organization-specific or newly discovered tools.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "CustomPatterns": ["my-custom-scanner", "internal-pentest-tool"]
    ///     </code>
    /// </example>
    public List<string> CustomPatterns { get; set; } = [];

    /// <summary>
    ///     Tool patterns to exclude from detection.
    ///     Use this if you need to allow specific security tools (e.g., for authorized pentests).
    /// </summary>
    /// <example>
    ///     <code>
    ///     "ExcludedPatterns": ["nessus"]  // Allow Nessus for internal security scanning
    ///     </code>
    /// </example>
    public List<string> ExcludedPatterns { get; set; } = [];

    /// <summary>
    ///     Categories of tools to detect.
    ///     By default, all categories are detected.
    ///     Set to empty list to detect all categories.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "EnabledCategories": ["SqlInjection", "ExploitFramework", "CredentialAttack"]
    ///     </code>
    /// </example>
    public List<string> EnabledCategories { get; set; } = [];

    /// <summary>
    ///     Redirect detected security tools to a honeypot endpoint instead of blocking.
    ///     When set, security tool requests are silently redirected to this URL
    ///     to gather intelligence on attack patterns.
    ///     Leave null to use standard blocking behavior.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "HoneypotRedirectUrl": "/api/honeypot"
    ///     </code>
    /// </example>
    public string? HoneypotRedirectUrl { get; set; }
}

// ==========================================
// Project Honeypot Configuration
// ==========================================

/// <summary>
///     Configuration for Project Honeypot HTTP:BL integration.
///     Uses DNS lookups to check IP reputation against Project Honeypot's
///     database of known harvesters, comment spammers, and suspicious visitors.
///     See: https://www.projecthoneypot.org/httpbl_api.php
///     Requires a free API key from: https://www.projecthoneypot.org/httpbl_configure.php
/// </summary>
public class ProjectHoneypotOptions
{
    /// <summary>
    ///     Enable Project Honeypot HTTP:BL lookups.
    ///     Requires AccessKey to be set.
    ///     Default: false (requires API key)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Your Project Honeypot HTTP:BL access key.
    ///     Get a free key from: https://www.projecthoneypot.org/httpbl_configure.php
    ///     Must be 12 lowercase alphanumeric characters.
    /// </summary>
    /// <example>
    ///     <code>
    ///     "AccessKey": "abcdefghijkl"
    ///     </code>
    /// </example>
    public string? AccessKey { get; set; }

    /// <summary>
    ///     Threat score threshold above which to consider an IP as high threat.
    ///     Scores range from 0-255 (logarithmic scale).
    ///     IPs above this threshold trigger early exit as verified bad bot.
    ///     Default: 25 (fairly conservative)
    /// </summary>
    /// <remarks>
    ///     Threat score guidelines:
    ///     - 0-24: Low threat
    ///     - 25-49: Medium threat
    ///     - 50-99: High threat
    ///     - 100+: Very high threat (rare)
    /// </remarks>
    public int HighThreatThreshold { get; set; } = 25;

    /// <summary>
    ///     Maximum age in days for considering a listing relevant.
    ///     IPs last seen more than this many days ago are given lower weight.
    ///     Default: 90 days
    /// </summary>
    public int MaxDaysAge { get; set; } = 90;

    /// <summary>
    ///     Timeout for DNS lookups in milliseconds.
    ///     DNS lookups should be fast; longer timeouts may indicate network issues.
    ///     Default: 1000ms (1 second)
    /// </summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>
    ///     Cache duration for lookup results in seconds.
    ///     Results are cached to avoid repeated DNS queries for the same IP.
    ///     Default: 1800 seconds (30 minutes)
    /// </summary>
    public int CacheDurationSeconds { get; set; } = 1800;

    /// <summary>
    ///     Skip lookup for local/private IP addresses.
    ///     These won't be in Project Honeypot anyway.
    ///     Default: true
    /// </summary>
    public bool SkipLocalIps { get; set; } = true;

    /// <summary>
    ///     Treat harvesters (email scrapers) as malicious.
    ///     Default: true
    /// </summary>
    public bool TreatHarvestersAsMalicious { get; set; } = true;

    /// <summary>
    ///     Treat comment spammers as malicious.
    ///     Default: true
    /// </summary>
    public bool TreatCommentSpammersAsMalicious { get; set; } = true;

    /// <summary>
    ///     Treat "suspicious" entries as suspicious (not necessarily malicious).
    ///     Default: true
    /// </summary>
    public bool TreatSuspiciousAsSuspicious { get; set; } = true;
}

/// <summary>
///     Qdrant vector database configuration for similarity search.
///     When enabled, replaces the file-backed HNSW index with Qdrant for
///     fuzzy multi-vector signature matching.
/// </summary>
public class QdrantOptions
{
    /// <summary>Enable Qdrant-backed similarity search (default: false = use HNSW file)</summary>
    public bool Enabled { get; set; }

    /// <summary>Qdrant gRPC endpoint (default: http://localhost:6334)</summary>
    public string Endpoint { get; set; } = "http://localhost:6334";

    /// <summary>Collection name for signature vectors</summary>
    public string CollectionName { get; set; } = "stylobot-signatures";

    /// <summary>Vector dimension for heuristic features (default: 64, matching FeatureVectorizer)</summary>
    public int VectorDimension { get; set; } = 64;

    /// <summary>Enable ML embeddings via ONNX (augments heuristic vectors with semantic similarity)</summary>
    public bool EnableEmbeddings { get; set; }

    /// <summary>ONNX model file name for embeddings (default: all-MiniLM-L6-v2.onnx)</summary>
    public string EmbeddingModel { get; set; } = "all-MiniLM-L6-v2.onnx";

    /// <summary>Embedding vector dimension (384 for all-MiniLM-L6-v2)</summary>
    public int EmbeddingDimension { get; set; } = 384;
}