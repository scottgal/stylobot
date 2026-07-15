namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Configuration for honeypot path detection -- the pre-detection
///     <see cref="HoneypotPathTagger"/> middleware and the Wave 0
///     <see cref="HoneypotLinkContributor"/>.
/// </summary>
/// <remarks>
///     <para>
///         Honeypot path catalog (<see cref="HoneypotPathDefinitions"/>) is
///         compile-time and curated from public scanner-path lists. These
///         options layer on top: opt out, exempt specific paths from the
///         Tier 2 signal, or add deployment-specific traps.
///     </para>
///     <para>
///         Tier 1 paths (zero-FP credentials/keys/dumps) are never
///         exempt-able -- if you have a legitimate reason to serve
///         <c>/.aws/credentials</c> on a public URL, exemption is the
///         wrong tool. The signal stands.
///     </para>
/// </remarks>
public sealed class HoneypotDetectionOptions
{
    public const string SectionName = "BotDetection:Honeypot";

    /// <summary>
    ///     Master switch for honeypot path detection. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Paths to suppress from the Tier 2 honeypot signal -- "actually
    ///     this endpoint is real on my site". Matched after normalisation
    ///     against the same glob rules as the catalog (trailing <c>*</c>
    ///     supported). Tier 1 paths ignore this list.
    /// </summary>
    /// <example>
    ///     Operator runs WordPress, so <c>/wp-login.php</c> is real:
    ///     <code>
    ///     "BotDetection": {
    ///       "Honeypot": {
    ///         "ExemptPaths": [ "/wp-login.php", "/wp-admin*" ]
    ///       }
    ///     }
    ///     </code>
    /// </example>
    public List<string> ExemptPaths { get; set; } = new();

    /// <summary>
    ///     Deployment-specific honeypot paths added to the Tier 2 catalog at
    ///     runtime. Useful when you've intentionally seeded fake endpoints
    ///     (e.g. a fake <c>/api/v2/users/export</c>) and want them to fire
    ///     the same honeypot signal. Same glob syntax as the catalog.
    /// </summary>
    public List<string> AdditionalPaths { get; set; } = new();

    /// <summary>
    ///     Randomised throttle applied when serving the honeypot response.
    ///     Defaults to a 200-1000 ms uniform jitter so the attacker can't
    ///     fingerprint detection by timing -- a fixed delay would tell them
    ///     "stylobot saw this", which defeats the holodeck.
    /// </summary>
    public HoneypotRateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    ///     How the honeypot responds to a matched trap path. Default:
    ///     <see cref="HoneypotResponseMode.EngagePack"/> -- serve realistic fake content from a matching
    ///     simulation pack (a 200), falling back to a generic 404. This ENGAGES the scanner so pack
    ///     canaries + behavioural signals accrue.
    ///     <para>
    ///     Set to <see cref="HoneypotResponseMode.Deflect404"/> to serve a fast, plain 404 instead: the
    ///     scanner sees an honest "not found" and de-prioritises the path rather than being encouraged
    ///     to keep probing by a 200 fake. Detection still fires (the honeypot signal is on its dedicated
    ///     pathway, NOT the response status), and the rate-limit 403 still protects against a flood.
    ///     Use this when the deployment values deflection over engagement -- e.g. a public marketing
    ///     site that isn't running the holodeck and doesn't want scanners lingering on fake 200s.
    ///     </para>
    /// </summary>
    public HoneypotResponseMode ResponseMode { get; set; } = HoneypotResponseMode.EngagePack;
}

/// <summary>How the honeypot action policy responds to a matched trap path.</summary>
public enum HoneypotResponseMode
{
    /// <summary>Serve fake pack content (200) when a pack matches, else a generic 404 -- engages the scanner.</summary>
    EngagePack,

    /// <summary>Serve a fast, plain 404 -- deflects the scanner so it stops probing rather than being encouraged by a 200 fake.</summary>
    Deflect404
}

/// <summary>
///     Randomised rate-limit applied by the honeypot response policy before
///     writing the fake response. Random uniform draw in
///     <c>[BaseDelayMs - JitterMs, BaseDelayMs + JitterMs]</c>; clamped to
///     a non-negative value.
/// </summary>
public sealed class HoneypotRateLimitOptions
{
    /// <summary>Enable the jittered delay. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Mean delay in milliseconds. Default 600 ms.</summary>
    public int BaseDelayMs { get; set; } = 600;

    /// <summary>
    ///     Half-width of the uniform draw. Default 400 ms (so the actual
    ///     delay ranges 200-1000 ms with default <c>BaseDelayMs</c>).
    /// </summary>
    public int JitterMs { get; set; } = 400;

    /// <summary>
    ///     Max honeypot engagements per (fingerprint or IP) per 60 seconds.
    ///     Over the cap the policy returns an immediate fast 403 with no
    ///     jitter and no fake body -- protects against attackers hammering
    ///     a single trap, and tells them less. Default 10. Set to 0 to
    ///     disable the per-minute cap.
    /// </summary>
    public int MaxHitsPerFingerprintPerMinute { get; set; } = 10;

    /// <summary>
    ///     Max honeypot engagements per (fingerprint or IP) per 3600 seconds.
    ///     Same fast-403 treatment as the per-minute cap when exceeded.
    ///     Default 60. Set to 0 to disable the per-hour cap.
    /// </summary>
    public int MaxHitsPerFingerprintPerHour { get; set; } = 60;
}
