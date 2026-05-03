namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
///     Extension point for providing additional configuration overrides beyond YAML manifests
///     and appsettings.json. Commercial products can register implementations to provide
///     per-target (endpoint, user, geo, ua-family) config overrides backed by a database,
///     with live updates via pub/sub.
/// </summary>
/// <remarks>
///     Resolution chain (most specific wins):
///     1. IConfigurationOverrideSource implementations (commercial - database-backed, per-target)
///     2. appsettings.json (BotDetection:Detectors:{Name}:*)
///     3. YAML manifest defaults
///     4. Built-in code defaults
///
///     Multiple <see cref="IConfigurationOverrideSource"/> can be registered. They are queried
///     in registration order (first match wins) and their changes flow back via
///     <see cref="WatchAsync"/> to invalidate the <see cref="IDetectorConfigProvider"/> cache.
///
///     FOSS ships with no implementations of this interface - the provider uses YAML + appsettings only.
///     Commercial packages register PostgreSQL + Redis-backed implementations.
/// </remarks>
public interface IConfigurationOverrideSource
{
    /// <summary>
    ///     Resolve a parameter override for a detector in a given request context.
    ///     Returns null if this source has no override for this lookup - the provider falls
    ///     through to the next source, then appsettings, then YAML, then the code default.
    /// </summary>
    /// <param name="detectorName">Detector name (e.g., "Heuristic", "Fail2Ban")</param>
    /// <param name="parameterName">Parameter name within the detector's config</param>
    /// <param name="context">Request context (path, user, geo, etc.) for per-target resolution</param>
    /// <param name="ct">Cancellation token</param>
    Task<object?> TryGetParameterAsync(
        string detectorName,
        string parameterName,
        ConfigResolutionContext context,
        CancellationToken ct = default);

    /// <summary>
    ///     Watch for config changes. Each yielded change invalidates the provider's cache
    ///     for the affected detector/parameter combination. Commercial implementations use
    ///     Redis pub/sub; test implementations can yield from a channel.
    /// </summary>
    IAsyncEnumerable<ConfigurationChangeNotification> WatchAsync(CancellationToken ct = default);

    /// <summary>
    ///     Priority ordering - lower values are queried first (more specific).
    ///     Default 100; commercial per-request overrides use 10, global overrides use 50.
    /// </summary>
    int Priority { get; }

    /// <summary>Human-readable name for diagnostics/logging.</summary>
    string Name { get; }
}

/// <summary>
///     Request context for per-target config resolution.
///     A config override can target any combination of these properties;
///     more specific targets (path + user) win over less specific (path alone, then global).
/// </summary>
public sealed record ConfigResolutionContext
{
    /// <summary>Request path (e.g., "/api/checkout")</summary>
    public string? Path { get; init; }

    /// <summary>Authenticated user identifier</summary>
    public string? UserId { get; init; }

    /// <summary>API key identifier (rotated, not the secret)</summary>
    public string? ApiKey { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code</summary>
    public string? CountryCode { get; init; }

    /// <summary>User-agent family (Chrome, Firefox, Googlebot, etc.)</summary>
    public string? UaFamily { get; init; }

    /// <summary>Active detection policy name (default, strict, learning, etc.)</summary>
    public string? StrategyName { get; init; }

    /// <summary>Empty context - global scope only.</summary>
    public static readonly ConfigResolutionContext Empty = new();
}

/// <summary>
///     Notification that a config value has changed. Sent by IConfigurationOverrideSource.WatchAsync
///     so the provider can invalidate its cache and signal detectors to re-read config.
/// </summary>
public sealed record ConfigurationChangeNotification
{
    /// <summary>Detector whose config changed (null = cross-detector change)</summary>
    public string? DetectorName { get; init; }

    /// <summary>Parameter path within the detector (e.g., "parameters.fail2ban_block_threshold")</summary>
    public required string ParameterPath { get; init; }

    /// <summary>Target scope of the change (null = global)</summary>
    public ConfigResolutionContext? Target { get; init; }

    /// <summary>New value (null = override removed, fall back to next source)</summary>
    public object? NewValue { get; init; }

    /// <summary>When the change occurred</summary>
    public DateTime ChangedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Optional: who changed it (for audit logging)</summary>
    public string? ChangedBy { get; init; }
}