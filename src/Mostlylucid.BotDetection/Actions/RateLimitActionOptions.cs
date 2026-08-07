namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Configuration for <see cref="RateLimitActionPolicy"/>. Carries the
///     token-bucket parameters plus the composable
///     <see cref="OverLimitAction"/> that fires when the bucket is empty.
/// </summary>
public sealed class RateLimitActionOptions
{
    /// <summary>
    ///     Steady-state allowance. A bucket at capacity will refill to
    ///     capacity in <c>60 * BurstSize / RequestsPerMinute</c> seconds
    ///     after the last consumed token.
    /// </summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>
    ///     Maximum bucket fill -- the burst allowance. A visitor who has
    ///     been silent can fire <see cref="BurstSize"/> requests
    ///     back-to-back before the per-minute steady-state kicks in.
    /// </summary>
    public int BurstSize { get; set; } = 10;

    /// <summary>
    ///     Name of the action policy invoked when this bucket overflows.
    ///     Must be registered in <see cref="IActionPolicyRegistry"/> by the
    ///     time this policy runs. Typical choices:
    ///     <list type="bullet">
    ///         <item><c>throttle-status</c> -- friendly bots get a polite
    ///             HTTP 429 + <c>Retry-After: 60</c>.</item>
    ///         <item><c>block-soft</c> -- AI / scraper bots get bounced
    ///             harder because they tend to ignore Retry-After.</item>
    ///         <item><c>logonly</c> -- shadow mode, record the would-be
    ///             over-limit decision without applying it.</item>
    ///     </list>
    /// </summary>
    public string OverLimitAction { get; set; } = "throttle-status";

    /// <summary>
    ///     Which identity to bill the bucket against. <see cref="RateLimitKey.Signature"/>
    ///     for friendly bots (stable, hard to rotate), <see cref="RateLimitKey.Ip"/>
    ///     for grey-area where signature might be evaded.
    /// </summary>
    public RateLimitKey KeyBy { get; set; } = RateLimitKey.Signature;

    /// <summary>
    ///     When true (default), surfaces <c>X-RateLimit-Policy</c> and
    ///     <c>X-RateLimit-Limit</c> response headers on allowed requests so
    ///     well-behaved clients can self-regulate. Set <c>false</c> for
    ///     stealth.
    /// </summary>
    public bool IncludeHeaders { get; set; } = true;

    // ===== Static presets used by ActionPolicyRegistry's built-in block =====

    /// <summary>
    ///     60 req/min, burst 10, falls back to <c>throttle-status</c>.
    ///     Default for search engines, monitoring bots, and verified-vendor
    ///     bots: visible, polite, predictable.
    /// </summary>
    public static RateLimitActionOptions Search => new()
    {
        RequestsPerMinute = 60,
        BurstSize = 10,
        OverLimitAction = "throttle-status",
        KeyBy = RateLimitKey.Signature,
    };

    /// <summary>
    ///     10 req/min, burst 2, falls back to <c>block-soft</c>. AI scrapers
    ///     are aggressive and tend to ignore <c>Retry-After</c>, so the
    ///     overflow path bounces harder than search.
    /// </summary>
    public static RateLimitActionOptions Ai => new()
    {
        RequestsPerMinute = 10,
        BurstSize = 2,
        OverLimitAction = "block-soft",
        KeyBy = RateLimitKey.Signature,
    };

    /// <summary>
    ///     30 req/min, burst 5, falls back to <c>throttle-status</c>. Social
    ///     preview bots (Slack, Facebook, Twitter, LinkedIn, Discord)
    ///     stampede on link-share events; the burst absorbs the spike.
    /// </summary>
    public static RateLimitActionOptions Social => new()
    {
        RequestsPerMinute = 30,
        BurstSize = 5,
        OverLimitAction = "throttle-status",
        KeyBy = RateLimitKey.Signature,
    };

    /// <summary>
    ///     6 req/min, burst 2, falls back to <c>throttle-status</c>. Uptime
    ///     checks shouldn't be polling faster than ~10s anyway; this cap
    ///     just makes the contract explicit.
    /// </summary>
    public static RateLimitActionOptions Monitoring => new()
    {
        RequestsPerMinute = 6,
        BurstSize = 2,
        OverLimitAction = "throttle-status",
        KeyBy = RateLimitKey.Signature,
    };

    /// <summary>
    ///     Docker/OCI registry clients (Harbor, container registries).
    ///     600 req/min, burst 100 — generous allowance for image pull bursts
    ///     (a cluster booting pulls many blobs in rapid succession).
    ///     Keyed by signature so the same authenticated client session stays together.
    /// </summary>
    public static RateLimitActionOptions Registry => new()
    {
        RequestsPerMinute = 600,
        BurstSize = 100,
        OverLimitAction = "throttle-status",
        KeyBy = RateLimitKey.Signature,
    };
}

/// <summary>
///     Which stable identity a token bucket bills against.
/// </summary>
public enum RateLimitKey
{
    /// <summary>
    ///     Bill against <see cref="Models.SignalKeys.PrimarySignature"/>.
    ///     Stable across IP changes (the same Mastodon instance keeps the
    ///     same signature across instances). Used for known-friendly bot
    ///     classes where signature is hard to evade.
    /// </summary>
    Signature,

    /// <summary>
    ///     Bill against <see cref="Microsoft.AspNetCore.Http.HttpContext.Connection"/>'s
    ///     remote IP. Cycles more easily but harder to spoof; used for
    ///     grey-area bot classes where signature evasion is a concern.
    /// </summary>
    Ip,

    /// <summary>
    ///     Bill against the network-prefix the request came from: <c>/24</c>
    ///     for IPv4, <c>/48</c> for IPv6 (the HAProxy / Cloudflare
    ///     conventions). Real botnets span thousands of IPs in a small
    ///     number of CIDR ranges; subnet-keying aggregates them into one
    ///     bucket so the burst capacity isn't multiplied by IP count.
    /// </summary>
    Subnet,

    /// <summary>
    ///     Bill against <see cref="Models.SignalKeys.IpAsn"/> from the
    ///     geo-detection signals. One bucket per Autonomous System. Useful
    ///     for hosting-network mitigation (cloud providers, datacenter
    ///     ASNs) where individual IPs rotate but the AS is stable.
    ///     Fails open when <see cref="Models.SignalKeys.IpAsn"/> is absent
    ///     so deployments without the geo contributor don't accidentally
    ///     route every visitor through one global bucket.
    /// </summary>
    Asn,

    /// <summary>
    ///     Composite of <see cref="Asn"/> and <see cref="Signature"/>.
    ///     One bucket per (ASN, signature) pair. Discriminates fingerprint
    ///     within an ASN while still aggregating IP rotation under one AS.
    ///     The real-botnet-effective key: a single bot running across
    ///     thousands of IPs in one cloud-hosting AS hits one bucket; a
    ///     friendly bot in the same AS with a different signature has its
    ///     own budget. Fails open when either ASN or signature is absent.
    /// </summary>
    AsnAndSignature,
}
