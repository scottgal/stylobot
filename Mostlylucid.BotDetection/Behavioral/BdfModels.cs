using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Behavioral;

/// <summary>
///     Behavioral Description Format (BDF) - Root scenario object.
///     A declarative description of how a client behaves over time, used for:
///     - Describing behavior patterns (not just single requests, but shape over time)
///     - Replaying behavior through load generators/test harnesses
///     - Comparing and asserting expected bot detection classifications
///     - Reverse-mapping from signatures to generate synthetic test scenarios
/// </summary>
public sealed record BdfScenario
{
    /// <summary>BDF format version</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    /// <summary>Unique scenario identifier</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human-readable description of the scenario</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>Metadata about the scenario (author, tags, timestamps)</summary>
    [JsonPropertyName("metadata")]
    public ScenarioMetadata? Metadata { get; init; }

    /// <summary>Client identity and fingerprint details</summary>
    [JsonPropertyName("client")]
    public required ClientConfig Client { get; init; }

    /// <summary>Expected bot detection classification</summary>
    [JsonPropertyName("expectation")]
    public ExpectationConfig? Expectation { get; init; }

    /// <summary>Behavior phases (different modes: browsing, scraping, polling, bursts)</summary>
    [JsonPropertyName("phases")]
    public required IReadOnlyList<BdfPhase> Phases { get; init; }
}

/// <summary>Scenario metadata (authorship, creation time, tags)</summary>
public sealed record ScenarioMetadata
{
    [JsonPropertyName("author")] public string? Author { get; init; }

    [JsonPropertyName("createdUtc")] public DateTime? CreatedUtc { get; init; }

    [JsonPropertyName("tags")] public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>Client identity configuration (IP, UA, headers, signature)</summary>
public sealed record ClientConfig
{
    /// <summary>Signature ID for correlation (if using signature-based detection)</summary>
    [JsonPropertyName("signatureId")]
    public string? SignatureId { get; init; }

    /// <summary>Client IP address</summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    /// <summary>User-Agent string</summary>
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    /// <summary>Additional HTTP headers</summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>Expected classification and detection results</summary>
public sealed record ExpectationConfig
{
    /// <summary>Expected classification (Human, Bot, Mixed)</summary>
    [JsonPropertyName("expectedClassification")]
    public string? ExpectedClassification { get; init; }

    /// <summary>Maximum allowed bot probability</summary>
    [JsonPropertyName("maxBotProbability")]
    public double? MaxBotProbability { get; init; }

    /// <summary>Minimum required bot probability</summary>
    [JsonPropertyName("minBotProbability")]
    public double? MinBotProbability { get; init; }

    /// <summary>Maximum allowed risk band</summary>
    [JsonPropertyName("maxRiskBand")]
    public string? MaxRiskBand { get; init; }

    /// <summary>Minimum required risk band</summary>
    [JsonPropertyName("minRiskBand")]
    public string? MinRiskBand { get; init; }
}

/// <summary>
///     Behavior phase - a time-bounded or request-bounded behavior mode.
///     Represents a chunk of behavior with specific characteristics:
///     - Timing patterns (fixed, jittered, bursty)
///     - Navigation style (UI graph, sequential, random, scanner)
///     - Error handling behavior
///     - Content patterns
/// </summary>
public sealed record BdfPhase
{
    /// <summary>Phase name/identifier</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Phase duration (e.g. "60s", "2m"). Mutually exclusive with RequestCount.</summary>
    [JsonPropertyName("duration")]
    public string? Duration { get; init; }

    /// <summary>Number of requests in this phase. Mutually exclusive with Duration.</summary>
    [JsonPropertyName("requestCount")]
    public int? RequestCount { get; init; }

    /// <summary>Number of parallel request streams</summary>
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; init; } = 1;

    /// <summary>Base requests per second per stream</summary>
    [JsonPropertyName("baseRateRps")]
    public double BaseRateRps { get; init; } = 1.0;

    /// <summary>Timing configuration (fixed, jittered, burst)</summary>
    [JsonPropertyName("timing")]
    public required TimingConfig Timing { get; init; }

    /// <summary>Navigation configuration (how URLs are chosen)</summary>
    [JsonPropertyName("navigation")]
    public required NavigationConfig Navigation { get; init; }

    /// <summary>Error interaction behavior</summary>
    [JsonPropertyName("errorInteraction")]
    public ErrorInteractionConfig? ErrorInteraction { get; init; }

    /// <summary>Content/payload configuration</summary>
    [JsonPropertyName("content")]
    public ContentConfig? Content { get; init; }
}

/// <summary>Timing configuration - controls the temporal waveform of requests</summary>
public sealed record TimingConfig
{
    /// <summary>Timing mode: "fixed", "jittered", or "burst"</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    /// <summary>Base rate in requests per second</summary>
    [JsonPropertyName("baseRateRps")]
    public double BaseRateRps { get; init; } = 1.0;

    /// <summary>Jitter standard deviation (seconds) for "jittered" mode</summary>
    [JsonPropertyName("jitterStdDevSeconds")]
    public double JitterStdDevSeconds { get; init; } = 0.0;

    /// <summary>Burst configuration for "burst" mode</summary>
    [JsonPropertyName("burst")]
    public BurstConfig? Burst { get; init; }
}

/// <summary>Burst timing configuration</summary>
public sealed record BurstConfig
{
    /// <summary>Number of requests per burst</summary>
    [JsonPropertyName("burstSize")]
    public int BurstSize { get; init; } = 10;

    /// <summary>Interval between bursts (seconds)</summary>
    [JsonPropertyName("burstIntervalSeconds")]
    public double BurstIntervalSeconds { get; init; } = 10.0;
}

/// <summary>Navigation configuration - controls how URLs are selected</summary>
public sealed record NavigationConfig
{
    /// <summary>Navigation mode: "ui_graph", "sequential", "random", or "scanner"</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    /// <summary>Starting path</summary>
    [JsonPropertyName("startPath")]
    public string StartPath { get; init; } = "/";

    /// <summary>UI graph profile reference (for ui_graph mode)</summary>
    [JsonPropertyName("uiGraphProfile")]
    public string? UiGraphProfile { get; init; }

    /// <summary>Path templates with weights</summary>
    [JsonPropertyName("paths")]
    public IReadOnlyList<PathTemplate>? Paths { get; init; }

    /// <summary>Probability of choosing a non-afforded URL (off-graph jumps)</summary>
    [JsonPropertyName("offGraphProbability")]
    public double OffGraphProbability { get; init; } = 0.0;
}

/// <summary>Path template with weight and parameter ranges</summary>
public sealed record PathTemplate
{
    /// <summary>URL template (e.g. "/products/{id}")</summary>
    [JsonPropertyName("template")]
    public required string Template { get; init; }

    /// <summary>Relative weight for selection</summary>
    [JsonPropertyName("weight")]
    public double Weight { get; init; } = 1.0;

    /// <summary>ID range for parameterized paths</summary>
    [JsonPropertyName("idRange")]
    public IdRange? IdRange { get; init; }
}

/// <summary>ID range for parameterized path templates</summary>
public sealed record IdRange
{
    [JsonPropertyName("min")] public int Min { get; init; }

    [JsonPropertyName("max")] public int Max { get; init; }
}

/// <summary>Error interaction configuration - how client responds to HTTP errors</summary>
public sealed record ErrorInteractionConfig
{
    /// <summary>Retry on 4xx errors</summary>
    [JsonPropertyName("retryOn4xx")]
    public bool RetryOn4xx { get; init; } = false;

    /// <summary>Retry on 5xx errors</summary>
    [JsonPropertyName("retryOn5xx")]
    public bool RetryOn5xx { get; init; } = true;

    /// <summary>Respect Retry-After header</summary>
    [JsonPropertyName("respectRetryAfter")]
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>Delay between retries</summary>
    [JsonPropertyName("retryDelay")]
    public string RetryDelay { get; init; } = "1s";

    /// <summary>Maximum number of retries</summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; init; } = 3;
}

/// <summary>Content/payload configuration</summary>
public sealed record ContentConfig
{
    /// <summary>Body mode: "none", "template", or "random"</summary>
    [JsonPropertyName("bodyMode")]
    public string BodyMode { get; init; } = "none";

    /// <summary>Body templates (for POST/PUT payloads)</summary>
    [JsonPropertyName("templates")]
    public IReadOnlyList<string>? Templates { get; init; }
}