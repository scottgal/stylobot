namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Result of bot detection analysis
/// </summary>
public class BotDetectionResult
{
    /// <summary>
    ///     Overall confidence score (0.0 to 1.0) that the request is from a bot
    /// </summary>
    public double ConfidenceScore { get; set; }

    /// <summary>
    ///     Whether this is classified as a bot (typically confidence > 0.7)
    /// </summary>
    public bool IsBot { get; set; }

    /// <summary>
    ///     List of reasons why the request was flagged as a bot
    /// </summary>
    public List<DetectionReason> Reasons { get; set; } = new();

    /// <summary>
    ///     Type of bot detected (if any)
    /// </summary>
    public BotType? BotType { get; set; }

    /// <summary>
    ///     Name of the identified bot (if known)
    /// </summary>
    public string? BotName { get; set; }

    /// <summary>
    ///     Processing time in milliseconds, fractional (microsecond resolution).
    ///     Detection often completes in well under 1ms; storing as integer ms rounded
    ///     every sub-ms request to 0, hiding the actual timing distribution. Source is
    ///     Stopwatch.Elapsed.TotalMilliseconds (double).
    /// </summary>
    public double ProcessingTimeMs { get; set; }
}

/// <summary>
///     Individual detection reason
/// </summary>
public class DetectionReason
{
    /// <summary>
    ///     Category of detection
    /// </summary>
    public required string Category { get; set; }

    /// <summary>
    ///     Specific detail about what was detected
    /// </summary>
    public required string Detail { get; set; }

    /// <summary>
    ///     Contribution to overall confidence (0.0 to 1.0)
    /// </summary>
    public double ConfidenceImpact { get; set; }
}

/// <summary>
///     Types of bots
/// </summary>
public enum BotType
{
    Unknown,
    SearchEngine,
    SocialMediaBot,
    MonitoringBot,
    Scraper,
    MaliciousBot,
    GoodBot,
    VerifiedBot,
    AiBot,
    Tool,
    ExploitScanner,
    ClickFraud,
    /// <summary>
    ///     Traffic originating from inside the local network -- loopback,
    ///     RFC1918, or the docker bridge -- that the operator owns by
    ///     definition. Detection still runs and the request still lands in
    ///     the dashboard so admins can see what their own service-to-service
    ///     calls / test runners / admin curls look like, but action policies
    ///     route Internal through <c>logonly</c> instead of <c>throttle-tools</c>
    ///     / <c>throttle-aggressive</c>: throttling the gateway's own admin
    ///     traffic was the cause of the BDF replay 429 loop and similar
    ///     dashboard-self-poll latency spikes. Distinct from
    ///     <see cref="Tool"/> so the dashboard label reads "Internal" instead
    ///     of "Tool", and from <see cref="BotDetectionOptions.InternalNetworkBotTypeActionPolicies"/>
    ///     which only overrides the action while keeping the original UA-
    ///     derived classification.
    /// </summary>
    Internal,

    /// <summary>
    ///     A container-registry / OCI Distribution Spec client (docker pull, containerd,
    ///     Harbor replication, buildkit, etc.) whose registry behaviour has been
    ///     CORROBORATED against the v2 protocol -- not merely claimed by user-agent.
    ///
    ///     <para>
    ///         Only <c>RegistryClientSensor</c>'s corroborated paths emit this. The
    ///         uncorroborated <c>registry.ua_only</c> path deliberately stays
    ///         <see cref="Tool"/>: a bare UA claim is trivially spoofable, and that
    ///         split IS the spoof guard. Distinguishing the type lets operators write
    ///         registry-specific policy instead of catching all of <see cref="Tool"/>.
    ///     </para>
    ///
    ///     <para>
    ///         Appended LAST deliberately. Persistence is string-only
    ///         (<c>LearnedPatternStore</c> round-trips via <c>ToString()</c> /
    ///         <c>Enum.TryParse</c>) and there are no <c>(int)</c> casts of
    ///         <see cref="BotType"/> anywhere in the tree, so ordinal position carries
    ///         no meaning and no data migration is required. Keep it that way: insert
    ///         new members at the end.
    ///     </para>
    /// </summary>
    RegistryClient
}