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
}
