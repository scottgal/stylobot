namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Configuration for a named API key with fine-grained detection policy overlay.
///     Keys act as detection policy overlays - detection still runs but with per-key
///     detector enable/disable, weight overrides, and action policy overrides.
/// </summary>
public class ApiKeyConfig
{
    /// <summary>
    ///     The secret value callers present - gRPC <c>x-sb-api-key</c> metadata or the
    ///     REST <c>X-SB-Api-Key</c> header. When null or empty, the dictionary key of
    ///     this entry in <see cref="BotDetectionOptions.ApiKeys"/> is used as the secret.
    ///     Set this explicitly when the secret should come from a secret store: the
    ///     dictionary key then becomes a stable, non-sensitive identifier and the secret
    ///     itself stays out of configuration keys (e.g. a Kubernetes <c>secretKeyRef</c>).
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    ///     Human-readable name for this key (e.g., "CI Pipeline", "Claude Test Harness").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Optional description of what this key is used for.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Whether this key is active. Set to false to revoke without removing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // ==========================================
    // Endpoint Permissions
    // ==========================================

    /// <summary>
    ///     Glob patterns for allowed paths. Empty = all paths allowed.
    ///     Evaluated before <see cref="DeniedPaths"/>.
    ///     Examples: ["/_stylobot/**", "/api/**"]
    /// </summary>
    public List<string> AllowedPaths { get; set; } = [];

    /// <summary>
    ///     Glob patterns for denied paths. Evaluated after <see cref="AllowedPaths"/>.
    ///     Examples: ["/admin/**"]
    /// </summary>
    public List<string> DeniedPaths { get; set; } = [];

    // ==========================================
    // Detection Policy Overlay
    // ==========================================

    /// <summary>
    ///     Detectors to skip when this key is used. Use ["*"] to disable all detectors.
    ///     Examples: ["BehavioralWaveform", "Behavioral", "AdvancedBehavioral"]
    /// </summary>
    public List<string> DisabledDetectors { get; set; } = [];

    /// <summary>
    ///     Detector weight overrides when this key is used.
    ///     Key = detector name, Value = weight multiplier.
    ///     Example: { "UserAgent": 0.1 }
    /// </summary>
    public Dictionary<string, double> WeightOverrides { get; set; } = new();

    /// <summary>
    ///     Override the base detection policy entirely by name.
    ///     If set, uses this named policy instead of the path-resolved one.
    /// </summary>
    public string? DetectionPolicyName { get; set; }

    /// <summary>
    ///     Override the action policy (e.g., "logonly" for monitoring keys).
    /// </summary>
    public string? ActionPolicyName { get; set; }

    // ==========================================
    // Time Controls
    // ==========================================

    /// <summary>
    ///     When this key expires. Null = never expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    ///     UTC time window when this key is valid, format "HH:mm-HH:mm".
    ///     Example: "09:00-17:00" for business hours only.
    ///     Null = no time restriction.
    /// </summary>
    public string? AllowedTimeWindow { get; set; }

    // ==========================================
    // Rate Limits
    // ==========================================

    /// <summary>
    ///     Per-key rate limit per minute. 0 = unlimited.
    ///     Independent of bot detection rate limiting.
    /// </summary>
    public int RateLimitPerMinute { get; set; }

    /// <summary>
    ///     Per-key rate limit per hour. 0 = unlimited.
    /// </summary>
    public int RateLimitPerHour { get; set; }

    // ==========================================
    // Metadata
    // ==========================================

    /// <summary>
    ///     Tags for categorization and filtering.
    ///     Examples: ["ci", "monitoring", "test-harness"]
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    ///     Future: bind key to a specific user identity.
    /// </summary>
    public string? BoundIdentity { get; set; }

    /// <summary>
    ///     When true, requests authenticated with this key produce NO learning
    ///     side-effects: the reputation cache is not updated, Markov transitions
    ///     are not recorded, and observations are not queued for absorption.
    ///     Detection still runs (so the key holder sees a normal verdict header
    ///     trail) but the request does not shape the model.
    ///     <para>
    ///         Set this for debug, smoke-test, monitoring, and CI keys -- anything
    ///         whose traffic is unrepresentative of real users. Real human or bot
    ///         traffic that flows through a key with this flag set will be invisible
    ///         to the centroid/reputation pipeline, so keep it OFF for production
    ///         tooling whose traffic you want the model to learn from.
    ///     </para>
    ///     <para>
    ///         API keys are debug-bypass credentials, NOT user-facing auth -- they
    ///         must be protected from disclosure. Anyone holding a key with this
    ///         flag set can browse the site without contributing to detection.
    ///     </para>
    /// </summary>
    public bool DisableLearningWrites { get; set; }
}

/// <summary>
///     Immutable context object for a validated API key, stored in HttpContext.Items
///     and flowing through the detection pipeline.
/// </summary>
public sealed record ApiKeyContext
{
    public required string KeyName { get; init; }
    public required IReadOnlyList<string> DisabledDetectors { get; init; }
    public required IReadOnlyDictionary<string, double> WeightOverrides { get; init; }
    public string? DetectionPolicyName { get; init; }
    public string? ActionPolicyName { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    ///     Mirrors <see cref="ApiKeyConfig.DisableLearningWrites"/>. When true,
    ///     the middleware skips enqueuing background enrichment, recording
    ///     Markov transitions, and queueing identity observations. Reads still
    ///     happen, so detection runs and the verdict header trail is honest;
    ///     only write-back into the model is suppressed.
    /// </summary>
    public bool DisableLearningWrites { get; init; }

    /// <summary>
    ///     Whether all detectors are disabled (key has ["*"] in DisabledDetectors).
    /// </summary>
    public bool DisablesAllDetectors => DisabledDetectors.Count == 1 && DisabledDetectors[0] == "*";
}